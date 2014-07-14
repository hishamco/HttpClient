﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Net.Http.Client
{
    public class HttpConnection : IDisposable
    {
        private const string CRLF = "\r\n";

        public HttpConnection(BufferedReadStream transport)
        {
            Transport = transport;
        }

        public BufferedReadStream Transport { get; private set; }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                // Serialize headers & send
                string rawRequest = SerializeRequest(request);
                byte[] requestBytes = Encoding.ASCII.GetBytes(rawRequest);
                await Transport.WriteAsync(requestBytes, 0, requestBytes.Length, cancellationToken);

                // TODO: Determin if there's a request body?
                // Wait for 100-continue?
                // Send body

                // Receive headers
                List<string> responseLines = await ReadResponseLinesAsync(cancellationToken);
                // Determine response type (Chunked, Content-Length, opaque, none...)
                // Receive body
                return CreateResponseMessage(responseLines);
            }
            catch (Exception ex)
            {
                Dispose(); // Any errors at this layer abort the connection.
                throw new HttpRequestException("The requested failed, see inner exception for details.", ex);
            }
        }

        private string SerializeRequest(HttpRequestMessage request)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(request.Method);
            builder.Append(' ');
            builder.Append(request.GetAddressLineProperty());
            builder.Append(" HTTP/");
            builder.Append(request.Version.ToString(2));
            builder.Append(CRLF);

            foreach (var header in request.Headers)
            {
                foreach (var value in header.Value)
                {
                    builder.Append(header.Key);
                    builder.Append(": ");
                    builder.Append(value);
                    builder.Append(CRLF);
                }
            }

            if (request.Content != null)
            {
                foreach (var header in request.Content.Headers)
                {
                    foreach (var value in header.Value)
                    {
                        builder.Append(header.Key);
                        builder.Append(": ");
                        builder.Append(value);
                        builder.Append(CRLF);
                    }
                }
            }
            // Headers end with an empty line
            builder.Append(CRLF);
            return builder.ToString();
        }

        private async Task<List<string>> ReadResponseLinesAsync(CancellationToken cancellationToken)
        {
            List<string> lines = new List<string>();
            string line = await Transport.ReadLineAsync(cancellationToken);
            while (line.Length > 0)
            {
                lines.Add(line);
                line = await Transport.ReadLineAsync(cancellationToken);
            }
            return lines;
        }

        private HttpResponseMessage CreateResponseMessage(List<string> responseLines)
        {
            string responseLine = responseLines.First();
            // HTTP/1.1 200 OK
            string[] responseLineParts = responseLine.Split(new[] { ' ' }, 3);
            // TODO: Verify HTTP/1.0 or 1.1.
            if (responseLineParts.Length < 2)
            {
                throw new HttpRequestException("Invalid response line: " + responseLine);
            }
            int statusCode = 0;
            if (int.TryParse(responseLineParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out statusCode))
            {
                // TODO: Validate range
            }
            else
            {
                throw new HttpRequestException("Invalid status code: " + responseLineParts[1]);
            }
            HttpResponseMessage response = new HttpResponseMessage((HttpStatusCode)statusCode);
            // TODO: Reason Phrase
            var content = new HttpConnectionResponseContent(this);
            response.Content = content;

            foreach (var rawHeader in responseLines.Skip(1))
            {
                int colonOffset = rawHeader.IndexOf(':');
                if (colonOffset <= 0)
                {
                    throw new HttpRequestException("The given header line format is invalid: " + rawHeader);
                }
                string headerName = rawHeader.Substring(0, colonOffset);
                string headerValue = rawHeader.Substring(colonOffset + 2);
                if (!response.Headers.TryAddWithoutValidation(headerName, headerValue))
                {
                    bool success = response.Content.Headers.TryAddWithoutValidation(headerName, headerValue);
                    System.Diagnostics.Debug.Assert(success, "Failed to add response header: " + rawHeader);
                }
            }
            // After headers have been set
            content.ResolveResponseStream(chunked: response.Headers.TransferEncodingChunked.HasValue && response.Headers.TransferEncodingChunked.Value);

            return response;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Transport.Dispose();
            }
        }
    }
}