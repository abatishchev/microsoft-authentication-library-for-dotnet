﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
#if NET6_0 || NET6_WIN
using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Identity.Client.Core;
using Microsoft.Identity.Client.Internal;

namespace Microsoft.Identity.Client.AppConfig
{
    /// <summary>
    /// Class to store crypto key information for a Managed Identity supported Azure resource.
    /// For more details see https://aka.ms/msal-net-managed-identity
    /// </summary>
    internal class TokenRequestAssertionInfo
    {
        private static TokenRequestAssertionInfo s_instance;
        private X509Certificate2 _bindingCertificate;
        private readonly CertificateCache _certificateCache;
        private static readonly object s_keyInfoLock = new (); // Lock object
        private readonly KeyMaterialInfo _keyMaterialInfo;
        private ILoggerAdapter _logger;

        private TokenRequestAssertionInfo(RequestContext requestContext)
        {
            _logger = requestContext.Logger;
            _keyMaterialInfo = new KeyMaterialInfo(requestContext.ServiceBundle.Config.ClientCapabilities != null 
                && requestContext.ServiceBundle.Config.ClientCapabilities.Any());

            _certificateCache = CertificateCache.Instance();
            _bindingCertificate = _certificateCache.GetOrAddCertificate(() => CreateCertificateFromCryptoKeyInfo(_keyMaterialInfo));
        }

        public static TokenRequestAssertionInfo GetCredentialInfo(RequestContext requestContext)
        {
            return s_instance ??= new TokenRequestAssertionInfo(requestContext);
        }

        public X509Certificate2 BindingCertificate => _bindingCertificate;

        private X509Certificate2 CreateCertificateFromCryptoKeyInfo(KeyMaterialInfo keyMaterialInfo)
        {
            lock (s_keyInfoLock) // Lock to ensure thread safety
            {
                if (_bindingCertificate != null)
                {
                    _logger.Verbose(() => "[Managed Identity] A cached binding certificate was available.");
                    return _bindingCertificate;
                }
            }

            if (keyMaterialInfo.ECDsaCngKey != null)
            {
                return CreateCngCertificate(keyMaterialInfo);
            }

            return CreateRsaCertificate(keyMaterialInfo);
        }

        private X509Certificate2 CreateCngCertificate(KeyMaterialInfo keyMaterialInfo)
        {
            string certSubjectname = keyMaterialInfo.ECDsaCngKey.Key.UniqueName;

            try
            {
                lock (s_keyInfoLock) // Lock to ensure thread safety
                {
                    ECDsaCng eCDsaCngKey = keyMaterialInfo.ECDsaCngKey;

                    _logger.Verbose(() => "[Managed Identity] Creating binding certificate with CNG key for credential endpoint.");

                    // Create a certificate request
                    CertificateRequest request = CreateCertificateRequest(certSubjectname, eCDsaCngKey);

                    // Create a self-signed X.509 certificate
                    DateTimeOffset startDate = DateTimeOffset.UtcNow;
                    DateTimeOffset endDate = startDate.AddYears(2); //expiry 

                    //Create the self signed cert
                    X509Certificate2 selfSigned = request.CreateSelfSigned(startDate, endDate);

                    //create the cert with just the public key
                    X509Certificate2 publicKeyOnlyCertificate = new X509Certificate2(selfSigned.Export(X509ContentType.Cert));

                    //now copy the private key to the cert
                    //this is needed for mtls schannel to work with in-memory certificates
                    X509Certificate2 authCertificate = AssociatePrivateKeyInfo(publicKeyOnlyCertificate, eCDsaCngKey);

                    _logger.Verbose(() => "[Managed Identity] Binding certificate (with cng key) created successfully.");

                    return authCertificate;
                }
            }
            catch (CryptographicException ex)
            {
                // Handle or log the exception
                _logger.Error($"Error generating binding certificate: {ex.Message}");
                throw;
            }
        }

        private X509Certificate2 CreateRsaCertificate(KeyMaterialInfo keyMaterialInfo)
        {
            string certSubjectname = "MsalinMemoryCertificate";

            try
            {
                lock (s_keyInfoLock) // Lock to ensure thread safety
                {
                    // Create an RSA key
                    using (RSA rsaKey = RSA.Create())
                    {
                        _logger.Verbose(() => "[Managed Identity] Creating binding certificate with CNG key for credential endpoint.");

                        // Create a certificate request
                        CertificateRequest request = CreateCertificateRequest(certSubjectname, rsaKey);

                        // Create a self-signed X.509 certificate
                        DateTimeOffset startDate = DateTimeOffset.UtcNow;
                        DateTimeOffset endDate = startDate.AddYears(2); //expiry 

                        //Create the self signed cert
                        X509Certificate2 selfSigned = request.CreateSelfSigned(startDate, endDate);

                        //export the pfx format cert
                        X509Certificate2 authCertificate = new X509Certificate2(selfSigned.Export(X509ContentType.Pkcs12));

                        _logger.Verbose(() => "[Managed Identity] Binding certificate (with rsa key) created successfully.");

                        return authCertificate;
                    }
                }
            }
            catch (CryptographicException ex)
            {
                // Handle or log the exception
                _logger.Error($"Error generating binding certificate: {ex.Message}");
                throw;
            }
        }

        private CertificateRequest CreateCertificateRequest(string subjectName, AsymmetricAlgorithm key)
        {
            CertificateRequest certificateRequest = null;

            _logger.Verbose(() => "[Managed Identity] Creating certificate request for the binding certificate.");

            if (key is ECDsaCng ecdsaKey)
            {
                certificateRequest = new(
                    $"CN={subjectName}", // Common Name 
                    ecdsaKey, // ECDsa key
                    HashAlgorithmName.SHA256); // Hash algorithm for the certificate
            }

            else if (key is RSA rsaKey)
            {
                certificateRequest = new(
                    $"CN={subjectName}", // Common Name 
                    rsaKey, // ECDsa key
                    HashAlgorithmName.SHA256, // Hash algorithm for the certificate
                    RSASignaturePadding.Pkcs1); //Signature padding
            }

            return certificateRequest;
        }

        private X509Certificate2 AssociatePrivateKeyInfo(X509Certificate2 publicKeyOnlyCertificate, ECDsaCng eCDsaCngKey)
        {
            _logger.Verbose(() => "[Managed Identity] Associating private key with the binding certificate.");
            return publicKeyOnlyCertificate.CopyWithPrivateKey(eCDsaCngKey);
        }

        public static string CreateCredentialPayload(X509Certificate2 x509Certificate2)
        {
            string certificateBase64 = Convert.ToBase64String(x509Certificate2.Export(X509ContentType.Cert));

            return @"
                    {
                        ""cnf"": {
                            ""jwk"": {
                                ""kty"": ""RSA"",
                                ""use"": ""sig"",
                                ""alg"": ""RS256"",
                                ""kid"": """ + x509Certificate2.Thumbprint + @""",
                                ""x5c"": [""" + certificateBase64 + @"""]
                            }
                        },
                        ""latch_key"": false    
                    }";
        }
    }
}
#endif
