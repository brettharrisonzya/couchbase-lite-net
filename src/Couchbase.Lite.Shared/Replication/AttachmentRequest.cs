using System;
using System.IO;
using System.Threading;
using Couchbase.Lite.Support;

namespace Couchbase.Lite.Internal
{
    internal class AttachmentRequest
    {
        private ManualResetEvent completeEvent;
        private MemoryStream stream;
        private bool error = false;
        private readonly object _locker = new object ();

        public Attachment attachment { get; private set; }
        public RemoteRequestProgress progress { get; set; }

        public AttachmentRequest(Attachment att)
        {
            attachment = att;
            completeEvent = new ManualResetEvent(false);
            stream = new MemoryStream();
        }

        public void WaitForComplete()
        {
            completeEvent.WaitOne();
        }

        public void SetComplete()
        {
            completeEvent.Set();
        }

        public void SetError()
        {
            error = true;
        }

        public void AppendData(long totalSize, byte[] buffer, int bytesRead)
        {
            lock(_locker)
            {
                if (totalSize > 0 && stream.Capacity < totalSize)
                {
                    stream.Capacity = (int)totalSize;
                }

                stream.Write(buffer, 0, bytesRead);
            }
        }

        public MemoryStream GetStream()
        {
            if (error == true)
            {
                return null;
            }

            byte[] buffer = new byte[40960];
            int read;

            MemoryStream output = new MemoryStream(stream.Capacity);

            lock(_locker)
            {
                var savedPostion = stream.Position;
                stream.Position = 0;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                }

                stream.Position = savedPostion;
            }

            return output;
        }

        public byte[] GetBuffer()
        {
            return stream.GetBuffer();
        }

        public long GetBufferLength()
        {
            return stream.Length;
        }
    }
}
