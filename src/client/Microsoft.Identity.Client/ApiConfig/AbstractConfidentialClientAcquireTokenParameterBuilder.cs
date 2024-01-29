﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client.ApiConfig.Executors;
using Microsoft.Identity.Client.AppConfig;
using Microsoft.Identity.Client.AuthScheme.PoP;

namespace Microsoft.Identity.Client
{
    /// <summary>
    /// Base class for confidential client application token request builders
    /// </summary>
    /// <typeparam name="T"></typeparam>
#if !SUPPORTS_CONFIDENTIAL_CLIENT
    [System.ComponentModel.EditorBrowsable(EditorBrowsableState.Never)]  // hide confidential client on mobile
#endif
    public abstract class AbstractConfidentialClientAcquireTokenParameterBuilder<T>
        : AbstractAcquireTokenParameterBuilder<T>
        where T : AbstractAcquireTokenParameterBuilder<T>
    {
        internal AbstractConfidentialClientAcquireTokenParameterBuilder(IConfidentialClientApplicationExecutor confidentialClientApplicationExecutor)
            : base(confidentialClientApplicationExecutor.ServiceBundle)
        {
            ClientApplicationBase.GuardMobileFrameworks();
            ConfidentialClientApplicationExecutor = confidentialClientApplicationExecutor;
        }

        internal abstract Task<AuthenticationResult> ExecuteInternalAsync(CancellationToken cancellationToken);

        /// <inheritdoc/>
        public override Task<AuthenticationResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            ClientApplicationBase.GuardMobileFrameworks();
            ValidateAndCalculateApiId();
            return ExecuteInternalAsync(cancellationToken);
        }

        /// <summary>
        /// Validates the parameters of the AcquireToken operation.        
        /// </summary>
        /// <exception cref="MsalClientException"></exception>
        protected override void Validate()
        {            
            // Confidential client must have a credential
            if (ServiceBundle?.Config.ClientCredential == null &&
                CommonParameters.OnBeforeTokenRequestHandler == null &&
                ServiceBundle?.Config.AppTokenProvider == null
                ) 
            {
                throw new MsalClientException(
                    MsalError.ClientCredentialAuthenticationTypeMustBeDefined,
                    MsalErrorMessage.ClientCredentialAuthenticationTypeMustBeDefined);
            }

            base.Validate();
        }

        internal IConfidentialClientApplicationExecutor ConfidentialClientApplicationExecutor { get; }

        /// <summary>
        ///  Modifies the token acquisition request so that the acquired token is a Proof-of-Possession token (PoP), rather than a Bearer token. 
        ///  PoP tokens are similar to Bearer tokens, but are bound to the HTTP request and to a cryptographic key, which MSAL can manage on Windows.
        ///  See https://aka.ms/msal-net-pop
        /// </summary>
        /// <param name="popAuthenticationConfiguration">Configuration properties used to construct a Proof-of-Possession request.</param>
        /// <returns>The builder.</returns>
        /// <remarks>
        /// <list type="bullet">
        /// <item><description>An Authentication header is automatically added to the request.</description></item>
        /// <item><description>The PoP token is bound to the HTTP request, more specifically to the HTTP method (GET, POST, etc.) and to the Uri (path and query, but not query parameters).</description></item>
        /// <item><description>MSAL creates, reads and stores a key in memory that will be cycled every 8 hours.</description></item>
        /// <item><description>This is an experimental API. The method signature may change in the future without involving a major version upgrade.</description></item>
        /// </list>
        /// </remarks>
        public T WithProofOfPossession(PoPAuthenticationConfiguration popAuthenticationConfiguration)
        {
            ValidateUseOfExperimentalFeature();

            CommonParameters.PopAuthenticationConfiguration = popAuthenticationConfiguration ?? throw new ArgumentNullException(nameof(popAuthenticationConfiguration));

            CommonParameters.AuthenticationScheme = new PopAuthenticationScheme(CommonParameters.PopAuthenticationConfiguration, ServiceBundle);

            return this as T;
        }

        /// <summary>
        /// Sends a token request over an MTLS (Mutual TLS) connection using the provided client certificate.
        /// This method is public and part of the S2S (server-to-server) token binding process, 
        /// allowing external clients to securely establish a mutual TLS connection.
        /// </summary>
        /// <remarks>
        /// This method should be used when setting up a secure token exchange via MTLS.
        /// It's important to ensure that the provided X509Certificate2 is valid and secure.
        /// Note that only the /token request will be transmitted over this MTLS connection.
        /// </remarks>
        /// <param name="certificate">An X509Certificate2 object representing the client certificate for MTLS.</param>
        /// <returns>An instance of the class, allowing for method chaining in a fluent interface style.</returns>
        public T WithMtlsCertificate(X509Certificate2 certificate)
        {
            CommonParameters.MtlsCertificate = certificate;
            return this as T;
        }
    }
}
