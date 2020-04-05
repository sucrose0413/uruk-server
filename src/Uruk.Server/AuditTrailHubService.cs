﻿using System;
using System.Buffers;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JsonWebToken;

namespace Uruk.Server
{
    public class AuditTrailHubService : IAuditTrailHubService
    {
        private static readonly JsonEncodedText invalidRequestJson = JsonEncodedText.Encode("invalid_request");
        private static readonly JsonEncodedText invalidKeyJson = JsonEncodedText.Encode("invalid_key");

        private readonly JwtReader _jwtReader;
        private readonly IAuditTrailSink _sink;
        private readonly IAuditTrailStore _store;
        private readonly IDuplicateStore? _duplicateStore;

        public AuditTrailHubService(IAuditTrailSink sink, IAuditTrailStore store, IDuplicateStore? duplicateStore = null)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _duplicateStore = duplicateStore;
            _jwtReader = new JwtReader();
        }

        public async Task<AuditTrailResponse> TryStoreAuditTrail(ReadOnlySequence<byte> buffer, AuditTrailHubRegistration registration, CancellationToken cancellationToken = default)
        {
            var result = _jwtReader.TryReadToken(buffer, registration.Policy);
            if (result.Succedeed)
            {
                var token = result.Token!.AsSecurityEventToken();

                var record = new AuditTrailRecord(buffer.IsSingleSegment ? buffer.FirstSpan.ToArray() : buffer.ToArray(), token, registration.ClientId);

                if (_duplicateStore == null || await _duplicateStore.TryAddAsync(record))
                {
                    if (!_sink.TryWrite(record))
                    {
                        // throttle ?
                        return new AuditTrailResponse
                        {
                            Error = JsonEncodedText.Encode("An error occurred when adding the event to the queue.")
                        };
                    }
                }

                return new AuditTrailResponse { Succeeded = true };
            }
            else
            {
                if ((result.Status & TokenValidationStatus.KeyError) == TokenValidationStatus.KeyError)
                {
                    return new AuditTrailResponse
                    {
                        Error = invalidKeyJson
                    };
                }
                else
                {
                    var description = result.Status switch
                    {
                        TokenValidationStatus.MalformedToken => "Malformed token.",
                        TokenValidationStatus.TokenReplayed => "Duplicated token.",
                        TokenValidationStatus.Expired => "Expired token.",
                        TokenValidationStatus.MissingEncryptionAlgorithm => "Missing encryption algorithm in the header.",
                        TokenValidationStatus.DecryptionFailed => "Unable to decrypt the token.",
                        TokenValidationStatus.NotYetValid => "The token is not yet valid.",
                        TokenValidationStatus.DecompressionFailed => "Unable to decompress the token.",
                        TokenValidationStatus.CriticalHeaderMissing => $"The critical header '{result.ErrorHeader}' is missing.",
                        TokenValidationStatus.CriticalHeaderUnsupported => $"The critical header '{result.ErrorHeader}' is not supported.",
                        TokenValidationStatus.InvalidClaim => $"The claim '{result.ErrorClaim}' is invalid.",
                        TokenValidationStatus.MissingClaim => $"The claim '{result.ErrorClaim}' is missing.",
                        TokenValidationStatus.InvalidHeader => $"The header '{result.ErrorHeader}' is invalid.",
                        TokenValidationStatus.MissingHeader => $"The header '{result.ErrorHeader}' is missing.",
                        _ => null
                    };

                    return new AuditTrailResponse
                    {
                        Error = invalidRequestJson,
                        Description = description
                    };
                }
            }
        }
    }
}
