// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Minimal SR stub for Bazel compilation of test CommonPath ASN1 files.

namespace System
{
    internal static partial class SR
    {
        internal static string Cryptography_Der_Invalid_Encoding => nameof(Cryptography_Der_Invalid_Encoding);
        internal static string Cryptography_Pkcs_InvalidSignatureParameters => nameof(Cryptography_Pkcs_InvalidSignatureParameters);
        internal static string Cryptography_Pkcs_PssParametersMgfHashMismatch => nameof(Cryptography_Pkcs_PssParametersMgfHashMismatch);
        internal static string Cryptography_Pkcs_PssParametersMgfNotSupported => nameof(Cryptography_Pkcs_PssParametersMgfNotSupported);
        internal static string Cryptography_Pkcs_PssParametersSaltMismatch => nameof(Cryptography_Pkcs_PssParametersSaltMismatch);
        internal static string Cryptography_UnknownHashAlgorithm => nameof(Cryptography_UnknownHashAlgorithm);

        internal static string Format(string resourceFormat, object? p1) =>
            string.Format(resourceFormat, p1);

        internal static string Format(string resourceFormat, object? p1, object? p2) =>
            string.Format(resourceFormat, p1, p2);
    }
}
