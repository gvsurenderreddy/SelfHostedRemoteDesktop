﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BPUtil;
using BPUtil.SimpleHttp;
using MasterServer.Database;
using SHRDLib;
using SHRDLib.NetCommand;

namespace MasterServer
{
	/// <summary>
	/// HostConnect class for SHRD's Master Server.
	/// </summary>
	public static class HostConnect
	{
		private static class ProtocolErrors
		{
			public const string AuthResponseLength = "Authentication response length must be greater than zero.";
			public const string SignatureLength = "Authentication response specified 0-length signature.";
			public const string SecurityKeyLength = "Authentication response specified 0-length security key.";
			public const string PublicKeyLength = "Authentication response specified 0-length public key.";
			public const string NameLength = "Authentication response specified 0-length computer name.";
			public const string SignatureVerificationFailed = "Signature verification failed.";
			public const string FailedToAddComputer = "Failed to add computer.";
			public const string AuthResponseCommand = "Authentication response must begin with Command.ClientAuthentication.";
		}
		/// <summary>
		/// A dictionary of currently connected Host Services.
		/// </summary>
		private static ConcurrentDictionary<int, HostConnectHandle> hosts = new ConcurrentDictionary<int, HostConnectHandle>();
		/// <summary>
		/// Implements the server-side part of Self Hosted Remote Desktop's HostConnect protocol.  Called by the web server when a remote Host Service POSTs to url /hostconnect
		/// </summary>
		/// <param name="p">The HttpProcessor instance.</param>
		public static HostConnectResult HandleHostService(HttpProcessor p)
		{
			p.tcpClient.ReceiveTimeout = 30000;
			p.tcpClient.SendTimeout = 30000;
			Logger.Info("Host connected");
			Computer computer = null;
			try
			{
				#region Authentication Protocol
				{
					// Auth 0) Send ClientAuthentication command code.
					p.tcpStream.WriteByte((byte)Command.ClientAuthentication);

					// Auth 1) Send authentication challenge.  This is an array of 32 random bytes which the Host Service must sign with its private key.
					byte[] authChallenge = ByteUtil.GenerateRandomBytes(32);
					p.tcpStream.Write(authChallenge, 0, authChallenge.Length);

					// Auth 2) Receive authentication reply.  This comes in as one big block so that data can be added to the end of the block in the future without breaking existing implementations.
					Command receivedCommand = (Command)ByteUtil.ReadNBytes(p.tcpStream, 1)[0];
					if (receivedCommand != Command.ClientAuthentication)
						return new HostConnectResult(ProtocolErrors.AuthResponseCommand);

					ushort authResponseLength = ByteUtil.ReadUInt16(p.tcpStream);
					if (authResponseLength == 0)
						return new HostConnectResult(ProtocolErrors.AuthResponseLength);
					using (MemoryDataStream authResponse = new MemoryDataStream(p.tcpStream, authResponseLength))
					{
						// Auth 2.1) Read authentication type.
						HostAuthenticationType authType = (HostAuthenticationType)authResponse.ReadByte();

						// Auth 2.2) Read the security key that was created when the host download was provisioned.
						int securityKeyLength = authResponse.ReadByte();
						if (securityKeyLength == 0)
							return new HostConnectResult(ProtocolErrors.SecurityKeyLength);
						string securityKey = authResponse.ReadUtf8(securityKeyLength);


						// Auth 2.3) Read the signature.
						ushort signatureLength = authResponse.ReadUInt16();
						if (signatureLength == 0 && authType == HostAuthenticationType.PermanentHost)
							return new HostConnectResult(ProtocolErrors.SignatureLength);
						byte[] signature = authResponse.ReadNBytes(signatureLength);

						// Auth 2.4) Read the public key. This is used to identify and authenticate the computer.
						ushort publicKeyLength = authResponse.ReadUInt16();
						if (publicKeyLength == 0 && authType == HostAuthenticationType.PermanentHost)
							return new HostConnectResult(ProtocolErrors.PublicKeyLength);
						string publicKey = authResponse.ReadUtf8(publicKeyLength);

						// Auth 2.5) Read the computer name.
						int nameLength = authResponse.ReadByte();
						if (nameLength == 0)
							return new HostConnectResult(ProtocolErrors.NameLength);
						string name = authResponse.ReadUtf8(nameLength);

						// Get computer from database
						computer = ServiceWrapper.db.GetComputerByPublicKey(publicKey);
						bool computerIsNew = computer == null;
						if (computerIsNew)
						{
							Logger.Info("computerIsNew: " + name);
							computer = new Computer();
							computer.PublicKey = publicKey;
							computer.Name = name; // Only set the name if this is the first time we've seen the computer.  Future name changes will only happen in the SHRD administration interface.
							computer.LastDisconnect = TimeUtil.GetTimeInMsSinceEpoch();
						}
						else
							Logger.Info("Existing computer is reconnecting: " + computer.ID + " (" + computer.Name + ")");

						// Auth 2.6) Read the Host version string.
						int hostVersionLength = authResponse.ReadByte();
						computer.AppVersion = authResponse.ReadUtf8(hostVersionLength);

						// Auth 2.7) Read the OS version string.
						int osVersionLength = authResponse.ReadByte();
						computer.OS = authResponse.ReadUtf8(osVersionLength);

						// Signature Verification
						if (authType == HostAuthenticationType.PermanentHost &&
							!IdentityVerification.VerifySignature(authChallenge, computer.PublicKey, signature))
							return new HostConnectResult(ProtocolErrors.SignatureVerificationFailed);

						// Add or update this Computer in the database.
						try
						{
							if (computerIsNew)
							{
								ServiceWrapper.db.AddComputer(computer);
								// TODO: Remove this TEMPORARY LOGIC to add this computer to Group 1
								ServiceWrapper.db.AddComputerGroupMembership(computer.ID, 2);
							}
							else
							{
								ServiceWrapper.db.UpdateComputer(computer);
								ServiceWrapper.db.AddComputerGroupMembership(computer.ID, 2);
							}
						}
						catch (ThreadAbortException) { throw; }
						catch (Exception ex)
						{
							Logger.Debug(ex);
							return new HostConnectResult(ProtocolErrors.FailedToAddComputer);
						}
					}
				}
				#endregion

				// This is the Master Server, which is responsible for sending a KeepAlive packet after 120 seconds of sending inactivity.  The Host Service will do the same on a 60 second interval.
				// A 75 second timeout means that disconnections should always be detected within 75 seconds of connection loss.  I don't know how long the underlying TCP stacks will wait, so this provides some measure of a guarantee that we don't wait excessively long.
				p.tcpClient.ReceiveTimeout = 75000; // 60 seconds + 15 seconds for bad network conditions.
				p.tcpClient.SendTimeout = 75000;

				Logger.Info("Host authenticated: (" + computer.ID + ") " + computer.Name);
				// Create a HostConnectHandle for this computer to take over responsibility for the connection.
				HostConnectHandle handle = new HostConnectHandle(computer.ID, p);
				hosts.AddOrUpdate(computer.ID, handle, (id, existing) =>
					{
						// We have a handle for this host already, so just disconnect the old one.  It probably just hasn't timed out yet.
						existing.Disconnect();
						return handle;
					});

				handle.ListenLoop();
			}
			catch (ThreadAbortException) { }
			catch (SocketException)
			{
				Logger.Info(computer == null ? "Host disconnected before authentication was complete" : ("Host disconnected: (" + computer.ID + ") " + computer.Name));
			}
			catch (EndOfStreamException) // ordinary socket disconnect
			{
				Logger.Info(computer == null ? "Host disconnected before authentication was complete" : ("Host disconnected: (" + computer.ID + ") " + computer.Name));
			}
			catch (Exception ex)
			{
				string additionalInfo = computer == null ? "Host was not authenticated" : ("Host (" + computer.ID + ") " + computer.Name);
				Logger.Debug(ex, "Host (" + computer.ID + ") " + computer.Name);
			}
			finally
			{
				if (computer != null && hosts.TryRemove(computer.ID, out HostConnectHandle existing))
					existing.Disconnect();
			}

			return new HostConnectResult();
		}
		/// <summary>
		/// If the specified computer is currently connected to this Master Server, returns its HostConnectHandle.  Otherwise, returns null. 
		/// </summary>
		/// <param name="computerId">The ID of the computer.</param>
		/// <returns></returns>
		public static HostConnectHandle GetOnlineComputer(int computerId)
		{
			if (hosts.TryGetValue(computerId, out HostConnectHandle host))
				return host;
			return null;
		}
	}
	/// <summary>
	/// Provides access to a remote Host Service.
	/// </summary>
	public class HostConnectHandle
	{
		/// <summary>
		/// The computer ID of the computer represented by this HostConnectHandle.
		/// </summary>
		public readonly int ComputerID;
		/// <summary>
		/// The DateTime.UtcNow value at the moment host authentication passed and this HostConnectHandle was created.
		/// </summary>
		public readonly DateTime ConnectTime;
		private Stream stream;
		private HttpProcessor p;
		/// <summary>
		/// Lock this object before writing to [stream] or p.rawOutputStream or any other stream instance inheriting from these.
		/// Do not attempt to read from the network stream except in the same thread as ListenLoop is running in.
		/// </summary>
		private object writeLock = new object();
		private bool disconnected = false;
		KeepAliveSender keepAlive;

		public HostConnectHandle(int computerId, HttpProcessor p)
		{
			this.ComputerID = computerId;
			this.p = p;
			this.stream = p.tcpStream;
			this.ConnectTime = DateTime.UtcNow;
			// Send KeepAlive packets every 120 seconds if no other packets have been sent.
			keepAlive = new KeepAliveSender("KeepAlive[" + computerId + "]", 120000, SendKeepalive, (ignoredArg) => Disconnect());
		}

		/// <summary>
		/// Listens for incoming traffic.  This should be the only thread which reads from the [stream].
		/// </summary>
		internal void ListenLoop()
		{
			#region Command Loop
			try
			{
				while (!disconnected && p.tcpClient.Connected)
				{
					Command command = (Command)ByteUtil.ReadNBytes(stream, 1)[0];
					switch (command)
					{
						case Command.HostStatus:
							{
								int hostStatusLength = ByteUtil.ReadInt32(stream);
								HostStatus hostStatus = new HostStatus(stream, hostStatusLength);

								break;
							}
						case Command.KeepAlive:
							Logger.Info("Received Command.KeepAlive from (" + ComputerID + ") " + ServiceWrapper.db.GetComputer(ComputerID).Name);
							break;
						default:
							ProtectedSend(() =>
							{
								keepAlive.NotifyPacketSending();
								stream.WriteByte((byte)Command.Error_CommandCodeUnknown);
							});
							break;
					}
				}
			}
			finally
			{
				Disconnect();
			}
			#endregion
		}
		public void RequestWebSocketProxy(IPAddress sourceIp, string strProxyKey)
		{
			ProtectedSend(() =>
			{
				keepAlive.NotifyPacketSending();
				stream.WriteByte((byte)Command.WebSocketConnectionRequest);

				// Write proxy key as string.  This is all the Host Service actually needs to be able to initiate the proxied web socket connection.
				ByteUtil.WriteUtf8_16(strProxyKey, stream);

				// Write the source IP address as a string.  A Host Service could find this useful for logging or filtering purposes.
				ByteUtil.WriteUtf8_16(sourceIp.ToString(), stream);
			});
		}
		/// <summary>
		/// If it is time to send a keepalive packet, sends a keepalive packet.
		/// </summary>
		private void SendKeepalive(KeepAliveSender keepAlive)
		{
			ProtectedSend(() =>
			{
				if (keepAlive.IsTimeToKeepalive()) // Test timing after aquiring writeLock, to prevent unnecessary keepalive packets if another packet was sending while we were obtaining the lock.
				{
					keepAlive.NotifyPacketSending();
					stream.WriteByte((byte)Command.KeepAlive);
				}
			});
		}
		/// <summary>
		/// Wrap in this all code which sends data on the socket. This ensures the "disconnected" flag is honored, that writeLock is obtained, and that disconnection is handled gracefully.
		/// </summary>
		/// <param name="action"></param>
		private void ProtectedSend(Action action)
		{
			if (disconnected)
				return;
			lock (writeLock)
			{
				try
				{
					action();
				}
				catch (SocketException) { Disconnect(); }
				catch (EndOfStreamException) { Disconnect(); }
			}
		}
		/// <summary>
		/// Causes Master Server to disconnect from the host.
		/// </summary>
		public void Disconnect()
		{
			if (disconnected)
				return;
			disconnected = true;
			Try.Catch_RethrowThreadAbort(keepAlive.Dispose);
			Try.Catch_RethrowThreadAbort(p.tcpClient.Close);
			Logger.Info("UpdateComputerLastDisconnectTime: " + ComputerID);
			Try.Catch_RethrowThreadAbort(() => ServiceWrapper.db.UpdateComputerLastDisconnectTime(ComputerID));
		}
	}
}