﻿//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: An example WebRTC application that can receive the audio stream
// from a WebRTC peer.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
// 
// History:
// 12 May 2020	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog;
using SIPSorcery.Net;
using SIPSorceryMedia;
using WebSocketSharp;
using WebSocketSharp.Net.WebSockets;
using WebSocketSharp.Server;

namespace WebRTCServer
{
    public class SDPExchange : WebSocketBehavior
    {
        public RTCPeerConnection PeerConnection;

        public event Func<WebSocketContext, Task<RTCPeerConnection>> WebSocketOpened;
        public event Func<RTCPeerConnection, string, Task> OnMessageReceived;

        public SDPExchange()
        { }

        protected override async void OnMessage(MessageEventArgs e)
        {
            await OnMessageReceived(PeerConnection, e.Data);
        }

        protected override async void OnOpen()
        {
            base.OnOpen();
            PeerConnection = await WebSocketOpened(this.Context);
        }

        protected override void OnClose(CloseEventArgs e)
        {
            base.OnClose(e);
            PeerConnection.Close("remote party close");
        }
    }

    class Program
    {
        private const string WEBSOCKET_CERTIFICATE_PATH = "certs/localhost.pfx";
        private const string DTLS_CERTIFICATE_PATH = "certs/localhost.pem";
        private const string DTLS_KEY_PATH = "certs/localhost_key.pem";
        private const string DTLS_CERTIFICATE_FINGERPRINT = "sha-256 C6:ED:8C:9D:06:50:77:23:0A:4A:D8:42:68:29:D0:70:2F:BB:C7:72:EC:98:5C:62:07:1B:0C:5D:CB:CE:BE:CD";
        private const int WEBSOCKET_PORT = 8081;

        private static Microsoft.Extensions.Logging.ILogger logger = SIPSorcery.Sys.Log.Logger;

        private static WebSocketServer _webSocketServer;

        static void Main()
        {
            Console.WriteLine("WebRTC Server Sample Program");
            Console.WriteLine("Press ctrl-c to exit.");

            // Plumbing code to facilitate a graceful exit.
            CancellationTokenSource exitCts = new CancellationTokenSource(); // Cancellation token to stop the SIP transport and RTP stream.
            ManualResetEvent exitMre = new ManualResetEvent(false);

            AddConsoleLogger();

            // Initialise OpenSSL & libsrtp, saves a couple of seconds for the first client connection.
            Console.WriteLine("Initialising OpenSSL and libsrtp...");
            DtlsHandshake.InitialiseOpenSSL();
            Srtp.InitialiseLibSrtp();

            // Start web socket.
            Console.WriteLine("Starting web socket server...");
            _webSocketServer = new WebSocketServer(IPAddress.Any, WEBSOCKET_PORT, true);
            _webSocketServer.SslConfiguration.ServerCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(WEBSOCKET_CERTIFICATE_PATH);
            _webSocketServer.SslConfiguration.CheckCertificateRevocation = false;
            //_webSocketServer.Log.Level = WebSocketSharp.LogLevel.Debug;
            _webSocketServer.AddWebSocketService<SDPExchange>("/", (sdpExchanger) =>
            {
                sdpExchanger.WebSocketOpened += SendSDPOffer;
                sdpExchanger.OnMessageReceived += WebSocketMessageReceived;
            });
            _webSocketServer.Start();

            Console.WriteLine($"Waiting for browser web socket connection to {_webSocketServer.Address}:{_webSocketServer.Port}...");

            // Ctrl-c will gracefully exit the call at any point.
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                exitMre.Set();
            };

            // Wait for a signal saying the call failed, was cancelled with ctrl-c or completed.
            exitMre.WaitOne();
        }

        private static async Task<RTCPeerConnection> SendSDPOffer(WebSocketContext context)
        {
            logger.LogDebug($"Web socket client connection from {context.UserEndPoint}.");

            RTCConfiguration pcConfiguration = new RTCConfiguration
            {
                certificates = new List<RTCCertificate>
                {
                    new RTCCertificate
                    {
                        X_CertificatePath = DTLS_CERTIFICATE_PATH,
                        X_KeyPath = DTLS_KEY_PATH,
                        X_Fingerprint = DTLS_CERTIFICATE_FINGERPRINT
                    }
                }
            };

            var peerConnection = new RTCPeerConnection(pcConfiguration);
            var dtls = new DtlsHandshake(DTLS_CERTIFICATE_PATH, DTLS_KEY_PATH);

            MediaStreamTrack audioTrack = new MediaStreamTrack("0", SDPMediaTypesEnum.audio, false, new List<SDPMediaFormat> { new SDPMediaFormat(SDPMediaFormatsEnum.PCMU) }, MediaStreamStatusEnum.RecvOnly);
            peerConnection.addTrack(audioTrack);

            peerConnection.OnReceiveReport += RtpSession_OnReceiveReport;
            peerConnection.OnSendReport += RtpSession_OnSendReport;

            peerConnection.OnTimeout += (mediaType) =>
            {
                peerConnection.Close("remote timeout");
                dtls.Shutdown();
            };

            peerConnection.onconnectionstatechange += (state) =>
            {
                if (state == RTCPeerConnectionState.closed || state == RTCPeerConnectionState.disconnected || state == RTCPeerConnectionState.failed)
                {
                    peerConnection.OnReceiveReport -= RtpSession_OnReceiveReport;
                    peerConnection.OnSendReport -= RtpSession_OnSendReport;
                }
                else if (state == RTCPeerConnectionState.connected)
                {
                    logger.LogDebug("Peer connection connected.");
                    peerConnection.OnRtpPacketReceived += (SDPMediaTypesEnum media, RTPPacket rtpPkt) => logger.LogDebug($"RTP {media} pkt received, SSRC {rtpPkt.Header.SyncSource}.");
                }
            };

            peerConnection.oniceconnectionstatechange += (state) =>
            {
                if (state == RTCIceConnectionState.connected)
                {
                    logger.LogDebug("Starting DTLS handshake task.");

                    bool dtlsResult = false;
                    Task.Run(async () => dtlsResult = await DoDtlsHandshake(peerConnection, dtls))
                    .ContinueWith((t) =>
                    {
                        logger.LogDebug($"dtls handshake result {dtlsResult}.");

                        if (dtlsResult)
                        {
                            var remoteEP = peerConnection.IceSession.ConnectedRemoteEndPoint;
                            peerConnection.SetDestination(SDPMediaTypesEnum.audio, remoteEP, remoteEP);
                        }
                        else
                        {
                            dtls.Shutdown();
                            peerConnection.Close("dtls handshake failed.");
                        }
                    });
                }
            };

            var offerSdp = await peerConnection.createOffer(null);
            await peerConnection.setLocalDescription(offerSdp);

            logger.LogDebug($"Sending SDP offer to client {context.UserEndPoint}.");
            logger.LogDebug(offerSdp.sdp);

            context.WebSocket.Send(offerSdp.sdp);

            return peerConnection;
        }

        private static async Task WebSocketMessageReceived(RTCPeerConnection peerConnection, string message)
        {
            try
            {
                if (peerConnection.remoteDescription == null)
                {
                    logger.LogDebug("Answer SDP: " + message);
                    await peerConnection.setRemoteDescription(new RTCSessionDescriptionInit { sdp = message, type = RTCSdpType.answer });
                }
                else
                {
                    logger.LogDebug("ICE Candidate: " + message);
                    await peerConnection.addIceCandidate(new RTCIceCandidateInit { candidate = message });
                }
            }
            catch (Exception excp)
            {
                logger.LogError("Exception WebSocketMessageReceived. " + excp.Message);
            }
        }

        /// <summary>
        /// Hands the socket handle to the DTLS context and waits for the handshake to complete.
        /// </summary>
        /// <param name="webRtcSession">The WebRTC session to perform the DTLS handshake on.</param>
        private static async Task<bool> DoDtlsHandshake(RTCPeerConnection peerConnection, DtlsHandshake dtls)
        {
            logger.LogDebug("DoDtlsHandshake started.");

            if (!File.Exists(DTLS_CERTIFICATE_PATH))
            {
                throw new ApplicationException($"The DTLS certificate file could not be found at {DTLS_CERTIFICATE_PATH}.");
            }
            else if (!File.Exists(DTLS_KEY_PATH))
            {
                throw new ApplicationException($"The DTLS key file could not be found at {DTLS_KEY_PATH}.");
            }

            int res = dtls.DoHandshakeAsServer((ulong)peerConnection.GetRtpChannel(SDPMediaTypesEnum.audio).RtpSocket.Handle);

            logger.LogDebug("DtlsContext initialisation result=" + res);

            if (dtls.IsHandshakeComplete())
            {
                logger.LogDebug("DTLS negotiation complete.");

                var srtpSendContext = new Srtp(dtls, false);
                var srtpReceiveContext = new Srtp(dtls, true);

                peerConnection.SetSecurityContext(
                    srtpSendContext.ProtectRTP,
                    srtpReceiveContext.UnprotectRTP,
                    srtpSendContext.ProtectRTCP,
                    srtpReceiveContext.UnprotectRTCP);

                //dtls.Shutdown();

                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Diagnostic handler to print out our RTCP sender/receiver reports.
        /// </summary>
        private static void RtpSession_OnSendReport(SDPMediaTypesEnum mediaType, RTCPCompoundPacket sentRtcpReport)
        {
            if (sentRtcpReport.Bye != null)
            {
                logger.LogDebug($"RTCP sent BYE {mediaType}.");
            }
            else if (sentRtcpReport.SenderReport != null)
            {
                var sr = sentRtcpReport.SenderReport;
                logger.LogDebug($"RTCP sent SR {mediaType}, ssrc {sr.SSRC}, pkts {sr.PacketCount}, bytes {sr.OctetCount}.");
            }
            else
            {
                if (sentRtcpReport.ReceiverReport.ReceptionReports?.Count > 0)
                {
                    var rrSample = sentRtcpReport.ReceiverReport.ReceptionReports.First();
                    logger.LogDebug($"RTCP sent RR {mediaType}, ssrc {rrSample.SSRC}, seqnum {rrSample.ExtendedHighestSequenceNumber}.");
                }
                else
                {
                    logger.LogDebug($"RTCP sent RR {mediaType}, no packets sent or received.");
                }
            }
        }

        /// <summary>
        /// Diagnostic handler to print out our RTCP reports from the remote WebRTC peer.
        /// </summary>
        private static void RtpSession_OnReceiveReport(SDPMediaTypesEnum mediaType, RTCPCompoundPacket recvRtcpReport)
        {
            if (recvRtcpReport.Bye != null)
            {
                logger.LogDebug($"RTCP recv BYE {mediaType}.");
            }
            else
            {
                var rr = recvRtcpReport.ReceiverReport?.ReceptionReports?.FirstOrDefault();
                if (rr != null)
                {
                    logger.LogDebug($"RTCP {mediaType} Receiver Report: SSRC {rr.SSRC}, pkts lost {rr.PacketsLost}, delay since SR {rr.DelaySinceLastSenderReport}.");
                }
                else
                {
                    logger.LogDebug($"RTCP {mediaType} Receiver Report: empty.");
                }
            }
        }

        /// <summary>
        ///  Adds a console logger. Can be omitted if internal SIPSorcery debug and warning messages are not required.
        /// </summary>
        private static void AddConsoleLogger()
        {
            var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
            var loggerConfig = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                .WriteTo.Console()
                .CreateLogger();
            loggerFactory.AddSerilog(loggerConfig);
            SIPSorcery.Sys.Log.LoggerFactory = loggerFactory;
        }
    }
}
