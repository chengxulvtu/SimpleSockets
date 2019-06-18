﻿using AsyncClientServer.Messaging;
using AsyncClientServer.Messaging.Metadata;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using AsyncClientServer.Messaging.Compression;
using AsyncClientServer.Messaging.Cryptography;

namespace AsyncClientServer.Client
{

	/// <summary>
	/// Event that triggers when a client is connected to server
	/// </summary>
	/// <param name="tcpClient"></param>
	public delegate void ConnectedHandler(SocketClient tcpClient);

	/// <summary>
	/// Event that is triggered when the client has disconnected from the server.
	/// </summary>
	public delegate void DisconnectedFromServerHandler(SocketClient tcpClient, string ipServer, int port);

	/// <summary>
	/// Event that triggers when client receives a message
	/// </summary>
	/// <param name="tcpClient"></param>
	/// <param name="msg"></param>
	public delegate void ClientMessageReceivedHandler(SocketClient tcpClient, string msg);

	/// <summary>
	/// Event that is triggered when client receives a custom header message.
	/// </summary>
	/// <param name="tcpClient"></param>
	/// <param name="msg"></param>
	public delegate void ClientCustomHeaderReceivedHandler(SocketClient tcpClient, string msg, string header);

	/// <summary>
	/// Event that triggers when client sends a message
	/// </summary>
	/// <param name="tcpClient"></param>
	/// <param name="close"></param>
	public delegate void ClientMessageSubmittedHandler(SocketClient tcpClient, bool close);

	/// <summary>
	/// Event that is triggered when a file is received from the server, returns the new file path
	/// </summary>
	/// <param name="tcpClient"></param>
	/// <param name="path"></param>
	public delegate void FileFromServerReceivedHandler(SocketClient tcpClient, string path);

	/// <summary>
	/// Event that is triggered when a message failed to send
	/// </summary>
	/// <param name="tcpClient"></param>
	/// <param name="exceptionMessage"></param>
	public delegate void DataTransferFailedHandler(SocketClient tcpClient,byte[] messageData, string exceptionMessage);

	/// <summary>
	/// Event that is triggered when a message has failed to send
	/// </summary>
	/// <param name="tcpClient"></param>
	/// <param name="exceptionMessage"></param>
	public delegate void ErrorHandler(SocketClient tcpClient, string exceptionMessage);

	/// <summary>
	/// Event that is triggered when a file is received from the server and show the progress.
	/// </summary>
	/// <param name="tcpClient"></param>
	/// <param name="bytesReceived"></param>
	/// <param name="messageSize"></param>
	public delegate void ProgressFileTransferHandler(SocketClient tcpClient, int bytesReceived, int messageSize);

	public abstract class SocketClient : AsyncSocket
	{
		//Protected variabeles
		protected Socket Listener;
		protected bool CloseClient;
		protected readonly ManualResetEvent ConnectedMre = new ManualResetEvent(false);
		protected readonly ManualResetEvent SentMre = new ManualResetEvent(false);
		protected IPEndPoint Endpoint;
		protected static System.Timers.Timer KeepAliveTimer;
		private bool _disconnectedInvoked;

		//Contains messages
		protected BlockingQueue<Message> BlockingMessageQueue = new BlockingQueue<Message>();

		//Tokensource to cancel running tasks
		protected CancellationTokenSource TokenSource { get; set; }
		protected CancellationToken Token { get; set; }

		/// <summary>
		/// This is how many seconds te client waits to try and reconnect to the server
		/// </summary>
		public int ReconnectInSeconds { get; protected set; }

		//Events
		public event ConnectedHandler Connected;
		public event ClientMessageReceivedHandler MessageReceived;
		public event ClientCustomHeaderReceivedHandler CustomHeaderReceived;
		public event ClientMessageSubmittedHandler MessageSubmitted;
		public event FileFromServerReceivedHandler FileReceived;
		public event ProgressFileTransferHandler ProgressFileReceived;
		public event DisconnectedFromServerHandler Disconnected;
		public event DataTransferFailedHandler MessageFailed;
		public event ErrorHandler ErrorThrown;

		/// <summary>
		/// Constructor
		/// Use StartClient() to start a connection to a server.
		/// </summary>
		protected SocketClient()
		{
			KeepAliveTimer = new System.Timers.Timer(15000);
			KeepAliveTimer.Elapsed += KeepAlive;
			KeepAliveTimer.AutoReset = true;
			KeepAliveTimer.Enabled = false;

			IsRunning = false;

			MessageEncryption = new Aes256();
			FileCompressor = new GZipCompression();
			FolderCompressor = new ZipCompression();
		}

		//Timer that tries reconnecting every x seconds
		private void KeepAlive(object source, ElapsedEventArgs e)
		{
			if (Token.IsCancellationRequested)
			{
				Close();
				ConnectedMre.Reset();
			} else if (!IsConnected())
			{
				Close();
				ConnectedMre.Reset();
				StartClient(Ip, Port, ReconnectInSeconds);
			}
		}
		

		/// <summary>
		/// Starts the client.
		/// <para>requires server ip, port number and how many seconds the client should wait to try to connect again. Default is 5 seconds</para>
		/// </summary>
		public abstract void StartClient(string ipServer, int port, int reconnectInSeconds = 5);

		//Convert string to IPAddress
		protected IPAddress GetIp(string ip)
		{
			try
			{
				return Dns.GetHostAddresses(ip).First();
			}
			catch (SocketException se)
			{
				throw new Exception("Invalid server IP", se);
			}
			catch (Exception ex)
			{
				throw new Exception("Error trying to get a valid IPAddress from string : " + ip, ex);
			}
		}

		/// <summary>
		/// Check if client is connected to server
		/// </summary>
		/// <returns>bool</returns>
		public bool IsConnected()
		{
			try
			{
				return !((Listener.Poll(1000, SelectMode.SelectRead) && (Listener.Available == 0)) || !Listener.Connected);
			}
			catch (Exception)
			{
				return false;
			}
		}


		/// <summary>
		/// Closes the client.
		/// </summary>
		public void Close()
		{
			try
			{
				ConnectedMre.Reset();
				TokenSource.Cancel();
				IsRunning = false;

				if (!IsConnected())
				{
					return;
				}

				Listener.Shutdown(SocketShutdown.Both);
				Listener.Close();
				Listener = null;
				InvokeDisconnected(this);
			}
			catch (SocketException se)
			{
				throw new Exception(se.ToString());
			}
		}

		/// <summary>
		/// Safely close client and break all connections to server.
		/// </summary>
		public override void Dispose()
		{
			Close();
			ConnectedMre.Dispose();
			SentMre.Dispose();
			KeepAliveTimer.Enabled = false;
			KeepAliveTimer.Dispose();
			TokenSource.Dispose();

			GC.SuppressFinalize(this);
		}
		

		//When client connects.
		protected abstract void OnConnectCallback(IAsyncResult result);


		#region Receiving Data



		/// <summary>
		/// Start receiving data from server.
		/// </summary>
		protected void Receive()
		{
			//Start receiving data
			var state = new SocketState(Listener);
			StartReceiving(state);
		}

		//When client receives message
		protected override void ReceiveCallback(IAsyncResult result)
		{
			try
			{
				if (IsConnected())
				{
					HandleMessage(result);
				}
			}
			catch (Exception ex)
			{
				throw new Exception(ex.ToString());
			}
		}

		#endregion

		#region Sending data

		//Gets called when a message has been sent to the server.
		protected abstract void SendCallback(IAsyncResult result);

		//Gets called when file is done sending
		protected abstract void SendCallbackPartial(IAsyncResult result);

		//Gets called when Filetransfer is completed.
		protected override void FileTransferCompleted(bool close, int id)
		{
			try
			{
				if (close)
					Close();
			}
			catch (SocketException se)
			{
				throw new SocketException(se.ErrorCode);
			}
			catch (ObjectDisposedException ode)
			{
				throw new ObjectDisposedException(ode.ObjectName, ode.Message);
			}
			catch (Exception ex)
			{
				throw new Exception(ex.Message, ex);
			}
			finally
			{
				MessageSubmitted?.Invoke(this, close);
				SentMre.Set();
			}

		}

		//Sends message from queue
		protected abstract void BeginSendFromQueue(Message message);

		//Loops the queue for messages that have to be sent.
		protected void SendFromQueue()
		{
			while (!Token.IsCancellationRequested)
			{

				ConnectedMre.WaitOne();

				if (IsConnected())
				{
					BlockingMessageQueue.TryDequeue(out var message);
					BeginSendFromQueue(message);
				}
				else
				{
					Close();
					ConnectedMre.Reset();
				}

			}
		}

		#endregion

		#region Invokes


		internal void InvokeMessage(string text)
		{
			MessageReceived?.Invoke(this,  text);
		}

		protected void InvokeMessageSubmitted(bool close)
		{
			MessageSubmitted?.Invoke(this, close);
		}

		internal void InvokeFileReceived(string filePath)
		{
			FileReceived?.Invoke(this, filePath);
		}

		internal void InvokeFileTransferProgress(int bytesReceived, int messageSize)
		{
			ProgressFileReceived?.Invoke(this, bytesReceived, messageSize);
		}

		internal void InvokeCustomHeaderReceived(string msg, string header)
		{
			CustomHeaderReceived?.Invoke(this, msg, header);
		}

		protected void InvokeConnected(SocketClient a)
		{
			IsRunning = true;
			Connected?.Invoke(a);
			_disconnectedInvoked = false;
		}

		protected void InvokeDisconnected(SocketClient a)
		{
			if (_disconnectedInvoked == false)
			{
				Disconnected?.Invoke(a, a.Ip, a.Port);
				_disconnectedInvoked = true;
			}
		}

		protected void InvokeMessageFailed(byte[] messageData, string exception)
		{
			MessageFailed?.Invoke(this, messageData, exception);
		}

		protected void InvokeErrorThrown(string exception)
		{
			ErrorThrown?.Invoke(this, exception);
		}

		#endregion

		#region Messaging

		/// <summary>
		/// Send bytes to the server
		/// </summary>
		/// <param name="msg">Message as a byte array</param>
		/// <param name="close">if you want to close the client after sending the message.</param>
		protected abstract void SendBytes(byte[] msg, bool close);

		#region Message
		/*=================================
		*
		*	MESSAGE
		*
		*===========================================*/

		/// <summary>
		/// Send a message to the server
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// </summary>
		/// <param name="message"></param>
		/// <param name="encryptMessage"></param>
		/// <param name="close"></param>
		public void SendMessage(string message, bool encryptMessage, bool close)
		{
			byte[] data = CreateByteMessage(message, encryptMessage);
			SendBytes(data, close);
		}

		/// <summary>
		/// Send a message to the server
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// </summary>
		/// <param name="message"></param>
		/// <param name="close"></param>
		public void SendMessage(string message, bool close)
		{
			SendMessage(message, false, close);
		}

		/// <summary>
		/// Send a message to the server asynchronous.
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// </summary>
		/// <param name="message"></param>
		/// <param name="encryptMessage"></param>
		/// <param name="close"></param>
		public async Task SendMessageAsync(string message, bool encryptMessage, bool close)
		{
			await Task.Run(() => SendMessage(message, encryptMessage, close));
		}

		/// <summary>
		/// Send a message to the server asynchronous.
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// <para>The message will be encrypted before sending.</para>
		/// </summary>
		/// <param name="message"></param>
		/// <param name="close"></param>
		public async Task SendMessageAsync(string message, bool close)
		{
			await Task.Run(() => SendMessage(message, close));
		}
		#endregion

		#region File
		/*=================================
		*
		*	FILE
		*
		*===========================================*/

		/// <summary>
		/// Send a file to server
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// <para>Simple way of sending large files over sockets</para>
		/// </summary>
		/// <param name="fileLocation"></param>
		/// <param name="remoteFileLocation"></param>
		/// <param name="encryptFile"></param>
		/// <param name="compressFile"></param>
		/// <param name="close"></param>
		public void SendFile(string fileLocation, string remoteFileLocation, bool encryptFile, bool compressFile, bool close)
		{
			Task.Run(() => SendFileAsync(fileLocation, remoteFileLocation, encryptFile, compressFile, close));
		}

		/// <summary>
		/// Send a file to server
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// <para>Simple way of sending large files over sockets</para>
		/// </summary>
		/// <param name="fileLocation"></param>
		/// <param name="remoteFileLocation"></param>
		/// <param name="close"></param>
		public void SendFile(string fileLocation, string remoteFileLocation, bool close)
		{
			SendFile(fileLocation, remoteFileLocation, false, true, close);
		}

		/// <summary>
		/// Send a file to server asynchronous.
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// <para>Simple way of sending large files over sockets</para>
		/// </summary>
		/// <param name="fileLocation"></param>
		/// <param name="remoteSaveLocation"></param>
		/// <param name="encryptFile"></param>
		/// <param name="compressFile"></param>
		/// <param name="close"></param>
		public async Task SendFileAsync(string fileLocation, string remoteSaveLocation, bool encryptFile, bool compressFile, bool close)
		{
			try
			{
				await CreateAndSendAsyncFileMessage(fileLocation, remoteSaveLocation, compressFile, encryptFile, close);
			}
			catch (Exception ex)
			{
				throw new Exception(ex.Message, ex);
			}


		}

		/// <summary>
		/// Send a file to server asynchronous.
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// <para>The file will be compressed before sending.</para>
		/// <para>Simple way of sending large files over sockets</para>
		/// </summary>
		/// <param name="fileLocation"></param>
		/// <param name="remoteFileLocation"></param>
		/// <param name="close"></param>
		public async Task SendFileAsync(string fileLocation, string remoteFileLocation, bool close)
		{
			await SendFileAsync(fileLocation, remoteFileLocation, false, true, close);
		}
		#endregion

		#region Folder
		/*=================================
		*
		*	FOLDER
		*
		*===========================================*/

		/// <summary>
		/// Sends a folder to the server.
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// <para>The folder will be compressed to a .zip file before sending.</para>
		/// <para>Simple way of sending a folder over sockets</para>
		/// </summary>
		/// <param name="folderLocation"></param>
		/// <param name="remoteFolderLocation"></param>
		/// <param name="encryptFolder"></param>
		/// <param name="close"></param>
		public void SendFolder(string folderLocation, string remoteFolderLocation, bool encryptFolder, bool close)
		{
			Task.Run(() => SendFolderAsync(folderLocation, remoteFolderLocation, encryptFolder, close));
		}

		/// <summary>
		/// Sends a folder to the server.
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// <para>The folder will be compressed before it will be sent.</para>
		/// <para>Simple way of sending a folder over sockets</para>
		/// </summary>
		/// <param name="folderLocation"></param>
		/// <param name="remoteFolderLocation"></param>
		/// <param name="close"></param>
		public void SendFolder(string folderLocation, string remoteFolderLocation, bool close)
		{
			SendFolder(folderLocation, remoteFolderLocation, false, close);
		}


		/// <summary>
		/// Sends a folder to the server asynchronous.
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// <para>The folder will be compressed to a .zip file before sending.</para>
		/// <para>Simple way of sending a folder over sockets</para>
		/// </summary>
		/// <param name="folderLocation"></param>
		/// <param name="remoteFolderLocation"></param>
		/// <param name="encryptFolder"></param>
		/// <param name="close"></param>
		public async Task SendFolderAsync(string folderLocation, string remoteFolderLocation, bool encryptFolder, bool close)
		{

			try
			{
				await CreateAndSendAsyncFolderMessage(folderLocation, remoteFolderLocation, encryptFolder, close);
			}
			catch (Exception ex)
			{
				throw new Exception(ex.Message, ex);
			}
		}

		/// <summary>
		/// Sends a folder to the server asynchronous.
		/// <para/>The close parameter indicates if the client should close after sending or not.
		/// <para>The folder will be encrypted and compressed before it will be sent.</para>
		/// <para>Simple way of sending a folder over sockets</para>
		/// </summary>
		/// <param name="folderLocation"></param>
		/// <param name="remoteFolderLocation"></param>
		/// <param name="close"></param>
		public async Task SendFolderAsync(string folderLocation, string remoteFolderLocation, bool close)
		{
			await SendFolderAsync(folderLocation, remoteFolderLocation, false, close);
		}
		#endregion

		#region Custom Header
		//CUSTOM HEADER//	

		/// <summary>
		/// Sends a message to the server with a custom header.
		/// </summary>
		/// <param name="message"></param>
		/// <param name="header"></param>
		/// <param name="close"></param>
		public void SendCustomHeaderMessage(string message, string header, bool close)
		{
			SendCustomHeaderMessage(message, header, false, close);
		}

		/// <summary>
		/// Sends a message to the server with a custom header.
		/// </summary>
		/// <param name="message"></param>
		/// <param name="header"></param>
		/// <param name="encrypt"></param>
		/// <param name="close"></param>
		public void SendCustomHeaderMessage(string message, string header, bool encrypt, bool close)
		{
			byte[] data = CreateByteCustomHeader(message, header, encrypt);
			SendBytes(data, close);
		}

		/// <summary>
		/// Sends a message to the server with a custom header
		/// </summary>
		/// <param name="message"></param>
		/// <param name="header"></param>
		/// <param name="close"></param>
		/// <returns></returns>
		public async Task SendCustomHeaderMessageAsync(string message, string header, bool close)
		{
			await Task.Run(() => SendCustomHeaderMessage(message, header, false, close));
		}

		/// <summary>
		/// Sends a message to the server with a custom header
		/// </summary>
		/// <param name="message"></param>
		/// <param name="header"></param>
		/// <param name="encrypt"></param>
		/// <param name="close"></param>
		/// <returns></returns>
		public async Task SendCustomHeaderMessageAsync(string message, string header, bool encrypt, bool close)
		{
			await Task.Run(() => SendCustomHeaderMessage(message, header, encrypt, close));
		}

		#endregion

		#endregion

	}
}
