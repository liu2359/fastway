﻿using System;
using System.IO;
using System.Threading;
using System.Collections.Generic;

namespace fastway
{
	public class Conn
	{
		private EndPoint p;
		private uint remoteID;

		internal uint id;
		internal bool closed;
		internal Queue<byte[]> waitRecv;
		internal Queue<byte[]> waitSend;

		public uint ID { get { return id; } }
		public uint RemoteID { get { return remoteID; } }

		public Conn(EndPoint p, uint id, uint remoteID)
		{
			this.p = p;
			this.id = id;
			this.remoteID = remoteID;
			this.waitRecv = new Queue<byte[]> ();

			if (id == 0) {
				this.waitSend = new Queue<byte[]> ();
			}
		}

		public byte[] Receive()
		{
			lock (this) {
				if (this.closed)
					return new byte[0];
				
				if (this.waitRecv.Count == 0)
					return null;
				
				return this.waitRecv.Dequeue ();
			}
		}

		public bool Send(byte[] msg)
		{
			lock (this) {
				if (this.closed)
					return false;

				if (this.id != 0)
					this.p.Send (this.id, msg);
				else
					this.waitSend.Enqueue (msg);
			}
			return true;
		}

		public void Close()
		{
			lock (this) {
				this.closed = true;
				this.p.Close (this.id, this);
			}
		}
	}

	public class EndPoint
	{
		private Stream s;
		private bool closed;
		private Queue<Conn> waitAccept;
		private Dictionary<uint /* remote id */, List<Conn>> dialWait;
		private Dictionary<uint /* conn id */, Conn> connections;

		public EndPoint (Stream s)
		{
			this.s = s;
			this.waitAccept = new Queue<Conn> ();
			this.dialWait = new Dictionary<uint, List<Conn>> ();
			this.connections = new Dictionary<uint, Conn>();

			this.MsgLoop ();
		}

		public void Close()
		{
			lock (this) {
				this.closed = true;
				foreach (KeyValuePair<uint, Conn> item in connections) {
					lock (item.Value) {
						item.Value.closed = true;
					}
				}
				this.s.Close ();
			}
		}

		public Conn Accept()
		{
			lock (this) {
				if (this.closed)
					return null;
				
				if (this.waitAccept.Count > 0) {
					return this.waitAccept.Dequeue ();
				}
			}
			return null;
		}

		public Conn Dial(uint remoteID) 
		{
			lock (this) {
				if (this.closed)
					return null;
				
				Conn conn = new Conn (this, 0, remoteID);

				byte[] buf = new byte[13];
				using (MemoryStream ms = new MemoryStream (buf)) {
					using (BinaryWriter bw = new BinaryWriter (ms)) {
						bw.Write ((uint)9);
						bw.Write ((uint)0);
						bw.Write ((byte)0);
						bw.Write (remoteID);
					}
				}

				if (!this.dialWait.ContainsKey (remoteID)) {
					this.dialWait.Add (remoteID, new List<Conn> ());
				}
				this.dialWait [remoteID].Add (conn);

				this.s.BeginWrite (buf, 0, buf.Length, (IAsyncResult result) => {
					this.s.EndWrite (result);
				}, remoteID);

				return conn;
			}
		}

		internal void Send(uint connID, byte[] msg)
		{
			byte[] buf = new byte[4 + 4 + msg.Length];
			using (MemoryStream ms = new MemoryStream (buf)) {
				using (BinaryWriter bw = new BinaryWriter (ms)) {
					bw.Write ((uint)4 + msg.Length);
					bw.Write (connID);
					bw.Write (msg);
				}
			}
			this.s.BeginWrite (buf, 0, buf.Length, (IAsyncResult result) => {
				this.s.EndWrite(result);
			}, null);
		}

		internal void Close(uint connID, Conn conn)
		{
			lock (this) {
				if (this.closed)
					return;
				
				if (connID != 0) {
					if (this.connections.ContainsKey (connID)) {
						this.connections.Remove (connID);
					}
				} else {
					List<Conn> q;
					if (this.dialWait.TryGetValue (conn.RemoteID, out q)) {
						q.Remove (conn);
					}
				}
			}

			byte[] buf = new byte[13];
			using (MemoryStream ms = new MemoryStream (buf)) {
				using (BinaryWriter bw = new BinaryWriter (ms)) {
					bw.Write ((uint)9);
					bw.Write ((uint)0);
					bw.Write ((byte)4);
					bw.Write (connID);
				}
			}

			this.s.BeginWrite (buf, 0, buf.Length, (IAsyncResult result) => {
				this.s.EndWrite(result);
			}, null);
		}

		private void MsgLoop()
		{
			byte[] head = new byte[4];
			this.s.BeginRead (head, 0, 4, (IAsyncResult result1) => {
				byte[] buf = (byte[])result1.AsyncState;
				this.s.EndRead(result1);

				// decode length
				int length;
				using (MemoryStream ms = new MemoryStream (buf)) {
					using (BinaryReader br = new BinaryReader (ms)) {
						length = (int)br.ReadUInt32 ();
					}
				}

				buf = new byte[length];
				this.s.BeginRead (buf, 0, length, (IAsyncResult result2) => {
					byte[] body = (byte[])result2.AsyncState;
					this.s.EndRead(result2);

					// decode conn id
					uint connID;
					using (MemoryStream ms = new MemoryStream (body)) {
						using (BinaryReader br = new BinaryReader (ms)) {
							connID = br.ReadUInt32 ();
						}
					}

					// dispatch message
					if (connID != 0) {
						Conn conn;
						lock (this) {
							if (!this.connections.TryGetValue(connID, out conn)) {
								this.Close(connID, null);
								goto END;
							}
						}
						lock (conn) {
							conn.waitRecv.Enqueue(body);
						}
						END:
						this.MsgLoop();
						return;
					}

					// handle command
					switch (body[4]) {
					case 1:
						this.HandleAcceptCmd(body);
						break;
					case 2:
						this.HandleConnectCmd(body);
						break;
					case 3:
						this.HandleRefuseCmd(body);
						break;
					case 4:
						this.HandleCloseCmd(body);
						break;
					case 5:
						this.HandlePingCmd();
						break;
					default:
						throw new Exception("Unsupported Gateway Command");
					}
					this.MsgLoop();
				}, buf);
			}, head);
		}

		private void HandleAcceptCmd(byte[] body)
		{
			uint connID;
			uint remoteID;
			using (MemoryStream ms = new MemoryStream (body, 5, 8)) {
				using (BinaryReader br = new BinaryReader (ms)) {
					connID = br.ReadUInt32 ();
					remoteID = br.ReadUInt32();
				}
			}

			Conn conn;

			lock (this) {
				List<Conn> q;
				if (!this.dialWait.TryGetValue (remoteID, out q) || q.Count == 0) {
					this.Close (connID, null);
					return;
				}
				conn = q [0];
				q.RemoveAt (0);
				this.connections.Add (connID, conn);
			}

			lock (conn) {
				conn.id = connID;
				while (conn.waitSend.Count > 0) {
					this.Send (connID, conn.waitSend.Dequeue ());
				}
			}
		}

		private void HandleConnectCmd(byte[] body)
		{
			uint connID;
			uint remoteID;
			using (MemoryStream ms = new MemoryStream (body, 5, 8)) {
				using (BinaryReader br = new BinaryReader (ms)) {
					connID = br.ReadUInt32 ();
					remoteID = br.ReadUInt32();
				}
			}

			lock (this) {
				Conn conn = new Conn (this, connID, remoteID);
				this.waitAccept.Enqueue (conn);
				this.connections.Add (connID, conn);
			}
		}

		private void HandleRefuseCmd(byte[] body)
		{
			uint remoteID;
			using (MemoryStream ms = new MemoryStream (body, 5, 4)) {
				using (BinaryReader br = new BinaryReader (ms)) {
					remoteID = br.ReadUInt32();
				}
			}

			lock (this) {
				List<Conn> q;
				if (this.dialWait.TryGetValue (remoteID, out q)) {
					Conn conn = q [0];
					q.RemoveAt (0);
					lock (conn) {
						conn.closed = true;
					}
				}
			}
		}

		private void HandleCloseCmd(byte[] body)
		{
			uint connID;
			using (MemoryStream ms = new MemoryStream (body, 5, 4)) {
				using (BinaryReader br = new BinaryReader (ms)) {
					connID = br.ReadUInt32();
				}
			}

			lock (this) {
				Conn conn;
				if (this.connections.TryGetValue(connID, out conn)) {
					lock (conn) {
						conn.closed = true;
					}
				}
			}
		}

		private void HandlePingCmd()
		{
			byte[] buf = new byte[8];
			using (MemoryStream ms = new MemoryStream (buf)) {
				using (BinaryWriter bw = new BinaryWriter (ms)) {
					bw.Write ((uint)5);
					bw.Write ((uint)0);
					bw.Write ((byte)5);
				}
			}
			this.s.BeginWrite (buf, 0, buf.Length, (IAsyncResult result) => {
				this.s.EndWrite(result);
			}, null);
		}
	}
}

