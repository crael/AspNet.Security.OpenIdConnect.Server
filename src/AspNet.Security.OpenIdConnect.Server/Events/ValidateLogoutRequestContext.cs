/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using AspNet.Security.OpenIdConnect.Primitives;
using Microsoft.AspNetCore.Http;

namespace AspNet.Security.OpenIdConnect.Server
{
    /// <summary>
    /// Provides context information used when validating a logout request.
    /// </summary>
    public class ValidateLogoutRequestContext : BaseValidatingContext
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ValidateLogoutRequestContext"/> class.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="options"></param>
        /// <param name="request"></param>
        public ValidateLogoutRequestContext(
            HttpContext context,
            OpenIdConnectServerOptions options,
            OpenIdConnectRequest request)
            : base(context, options, request)
        {
            // Note: if the optional post_logout_redirect_uri parameter
            // is missing, mark the validation context as skipped.
            // See http://openid.net/specs/openid-connect-session-1_0.html#RPLogout
            if (string.IsNullOrEmpty(request.PostLogoutRedirectUri))
            {
                Skip();
            }

            PostLogoutRedirectUri = request.PostLogoutRedirectUri;
        }

        /// <summary>
        /// Gets the post_logout_redirect_uri specified by the client application.
        /// </summary>
        public string PostLogoutRedirectUri { get; private set; }

        /// <summary>
        /// Marks the context as skipped by the application.
        /// </summary>
        public override void Skip()
        {
            PostLogoutRedirectUri = null;

            base.Skip();
        }

        /// <summary>
        /// Checks the redirect URI to determine whether it equals <see cref="PostLogoutRedirectUri"/>.
        /// </summary>
        /// <param name="address"></param>
        public void Validate(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("The post_logout_redirect_uri cannot be null or empty.", nameof(address));
            }

            // Don't allow validation to alter the post_logout_redirect_uri parameter extracted
            // from the request if the address was explicitly provided by the client application.
            if (!string.IsNullOrEmpty(Request.PostLogoutRedirectUri) &&
                !string.Equals(Request.PostLogoutRedirectUri, address, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The end session request cannot be validated because a different " +
                    "post_logout_redirect_uri was specified by the client application.");
            }

            PostLogoutRedirectUri = address;

            base.Validate();
        }
    }
}
