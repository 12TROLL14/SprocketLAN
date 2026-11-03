using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SprocketMP.Net
{
    /// <summary>One framed TCP connection ([int32 length][payload]). Reader + writer threads; delivers to a shared inbox.</summary>
    public class Connection
    {
        static int _nextKey = 1;
        public readonly int Key = Interlocked.Increment(ref _nextKey);
        public byte PlayerId = 255;        // assigned by host after Hello
        public string Remote = "?";
        public volatile bool Closed;

        readonly TcpClient _tcp;
        readonly NetworkStream _stream;
        readonly ConcurrentQueue<byte[]> _out = new ConcurrentQueue<byte[]>();
        readonly AutoResetEvent _outSignal = new AutoResetEvent(false);
        readonly ConcurrentQueue<Inbound> _inbox;

        public struct Inbound { public Connection From; public byte[] Data; public bool Disconnect; }

        public volatile int RttMs = -1;
        public Connection(TcpClient tcp, ConcurrentQueue<Inbound> inbox)
        {
            _tcp = tcp; _inbox = inbox;
            _tcp.NoDelay = true;
            _tcp.ReceiveBufferSize = 1 << 16; _tcp.SendBufferSize = 1 << 16;
            _stream = tcp.GetStream();
            try { Remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?"; } catch { }
            new Thread(ReadLoop) { IsBackground = true, Name = "SprocketMP-read" }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "SprocketMP-write" }.Start();
        }

        public void Send(byte[] data)
        {
            if (Closed) return;
            if (_out.Count > 2000) { MPLog.Warn($"Send queue overflow for {Remote}, closing"); Close(); return; }
            _out.Enqueue(data); _outSignal.Set();
        }

        void WriteLoop()
        {
            var len = new byte[4];
            try
            {
                while (!Closed)
                {
                    _outSignal.WaitOne(100);
                    while (_out.TryDequeue(out var d))
                    {
                        BitConverter.TryWriteBytes(len, d.Length);
                        _stream.Write(len, 0, 4);
                        _stream.Write(d, 0, d.Length);
                    }
                    _stream.Flush();
                }
            }
            catch (Exception e) { if (!Closed) MPLog.Warn($"write {Remote}: {e.Message}"); Close(); }
        }

        void ReadLoop()
        {
            var len = new byte[4];
            try
            {
                while (!Closed)
                {
                    ReadExact(len, 4);
                    int n = BitConverter.ToInt32(len, 0);
                    if (n <= 0 || n > 1 << 20) throw new IOException("bad frame length " + n);
                    var buf = new byte[n];
                    ReadExact(buf, n);
                    // ping/pong answered right here on the network thread, so the measured ping is the real
                    // network round trip, not the time until the game's next frame picks the packet up
                    if (n == 9 && buf[0] == (byte)Msg.Ping) { var pong = (byte[])buf.Clone(); pong[0] = (byte)Msg.Pong; Send(pong); continue; }
                    if (n == 9 && buf[0] == (byte)Msg.Pong)
                    {
                        long sent = BitConverter.ToInt64(buf, 1);
                        RttMs = (int)Math.Max(0, (System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000L / System.Diagnostics.Stopwatch.Frequency);
                    }
                    _inbox.Enqueue(new Inbound { From = this, Data = buf });
                }
            }
            catch (Exception e) { if (!Closed) MPLog.Info($"connection {Remote} ended: {e.Message}"); }
            Close();
        }

        void ReadExact(byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = _stream.Read(buf, got, n - got);
                if (r <= 0) throw new IOException("closed by peer");
                got += r;
            }
        }

        public void Close()
        {
            if (Closed) return;
            Closed = true;
            try { _tcp.Close(); } catch { }
            _outSignal.Set();
            _inbox.Enqueue(new Inbound { From = this, Disconnect = true });
        }
    }

    public class HostListener
    {
        TcpListener _listener;
        volatile bool _running;
        readonly ConcurrentQueue<Connection.Inbound> _inbox;
        public readonly ConcurrentQueue<Connection> Accepted = new ConcurrentQueue<Connection>();

        public HostListener(ConcurrentQueue<Connection.Inbound> inbox) { _inbox = inbox; }

        public void Start(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "SprocketMP-accept" }.Start();
        }

        void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var c = _listener.AcceptTcpClient();
                    Accepted.Enqueue(new Connection(c, _inbox));
                }
                catch (Exception e) { if (_running) MPLog.Warn("accept: " + e.Message); }
            }
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
        }
    }
}
