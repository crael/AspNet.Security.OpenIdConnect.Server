/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AspNet.Security.OpenIdConnect.Extensions;
using AspNet.Security.OpenIdConnect.Primitives;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json.Linq;

namespace AspNet.Security.OpenIdConnect.Server
{
    public partial class OpenIdConnectServerHandler : AuthenticationHandler<OpenIdConnectServerOptions>
    {
        private async Task<bool> InvokeUserinfoEndpointAsync()
        {
            OpenIdConnectRequest request;

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                request = new OpenIdConnectRequest(Request.Query);
            }

            else if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                // Note: if no Content-Type header was specified, assume the userinfo request
                // doesn't contain any parameter and create an empty OpenIdConnectRequest.
                if (string.IsNullOrEmpty(Request.ContentType))
                {
                    request = new OpenIdConnectRequest();
                }

                else
                {
                    // May have media/type; charset=utf-8, allow partial match.
                    if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogError("The userinfo request was rejected because an invalid 'Content-Type' " +
                                        "header was received: {ContentType}.", Request.ContentType);

                        return await SendUserinfoResponseAsync(new OpenIdConnectResponse
                        {
                            Error = OpenIdConnectConstants.Errors.InvalidRequest,
                            ErrorDescription = "A malformed userinfo request has been received: " +
                                "the 'Content-Type' header contained an unexcepted value. " +
                                "Make sure to use 'application/x-www-form-urlencoded'."
                        });
                    }

                    request = new OpenIdConnectRequest(await Request.ReadFormAsync());
                }
            }

            else
            {
                Logger.LogError("The userinfo request was rejected because an invalid " +
                                "HTTP method was received: {Method}.", Request.Method);

                return await SendUserinfoResponseAsync(new OpenIdConnectResponse
                {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed userinfo request has been received: " +
                                       "make sure to use either GET or POST."
                });
            }

            // Note: set the message type before invoking the ExtractUserinfoRequest event.
            request.SetProperty(OpenIdConnectConstants.Properties.MessageType,
                                OpenIdConnectConstants.MessageTypes.UserinfoRequest);

            // Insert the userinfo request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            var @event = new ExtractUserinfoRequestContext(Context, Options, request);
            await Options.Provider.ExtractUserinfoRequest(@event);

            if (@event.HandledResponse)
            {
                Logger.LogDebug("The userinfo request was handled in user code.");

                return true;
            }

            else if (@event.Skipped)
            {
                Logger.LogDebug("The default userinfo request handling was skipped from user code.");

                return false;
            }

            else if (@event.IsRejected)
            {
                Logger.LogError("The userinfo request was rejected with the following error: {Error} ; {Description}",
                                /* Error: */ @event.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                                /* Description: */ @event.ErrorDescription);

                return await SendUserinfoResponseAsync(new OpenIdConnectResponse
                {
                    Error = @event.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = @event.ErrorDescription,
                    ErrorUri = @event.ErrorUri
                });
            }

            Logger.LogInformation("The userinfo request was successfully extracted " +
                                  "from the HTTP request: {Request}", request);

            string token;
            if (!string.IsNullOrEmpty(request.AccessToken))
            {
                token = request.AccessToken;
            }

            else
            {
                string header = Request.Headers[HeaderNames.Authorization];
                if (string.IsNullOrEmpty(header))
                {
                    Logger.LogError("The userinfo request was rejected because " +
                                    "the 'Authorization' header was missing.");

                    return await SendUserinfoResponseAsync(new OpenIdConnectResponse
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });
                }

                if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogError("The userinfo request was rejected because the " +
                                    "'Authorization' header was invalid: {Header}.", header);

                    return await SendUserinfoResponseAsync(new OpenIdConnectResponse
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });
                }

                token = header.Substring("Bearer ".Length);
                if (string.IsNullOrEmpty(token))
                {
                    Logger.LogError("The userinfo request was rejected because the access token was missing.");

                    return await SendUserinfoResponseAsync(new OpenIdConnectResponse
                    {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });
                }
            }

            var context = new ValidateUserinfoRequestContext(Context, Options, request);
            await Options.Provider.ValidateUserinfoRequest(context);

            if (context.HandledResponse)
            {
                Logger.LogDebug("The userinfo request was handled in user code.");

                return true;
            }

            else if (context.Skipped)
            {
                Logger.LogDebug("The default userinfo request handling was skipped from user code.");

                return false;
            }

            else if (!context.IsValidated)
            {
                Logger.LogError("The userinfo request was rejected with the following error: {Error} ; {Description}",
                                /* Error: */ context.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                                /* Description: */ context.ErrorDescription);

                return await SendUserinfoResponseAsync(new OpenIdConnectResponse
                {
                    Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = context.ErrorDescription,
                    ErrorUri = context.ErrorUri
                });
            }

            Logger.LogInformation("The userinfo request was successfully validated.");

            var ticket = await DeserializeAccessTokenAsync(token, request);
            if (ticket == null)
            {
                Logger.LogError("The userinfo request was rejected because the access token was invalid.");

                // Note: an invalid token should result in an unauthorized response
                // but returning a 401 status would invoke the previously registered
                // authentication middleware and potentially replace it by a 302 response.
                // To work around this limitation, a 400 error is returned instead.
                // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoError
                return await SendUserinfoResponseAsync(new OpenIdConnectResponse
                {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Invalid token."
                });
            }

            if (ticket.Properties.ExpiresUtc.HasValue &&
                ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow)
            {
                Logger.LogError("The userinfo request was rejected because the access token was expired.");

                // Note: an invalid token should result in an unauthorized response
                // but returning a 401 status would invoke the previously registered
                // authentication middleware and potentially replace it by a 302 response.
                // To work around this limitation, a 400 error is returned instead.
                // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoError
                return await SendUserinfoResponseAsync(new OpenIdConnectResponse
                {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Expired token."
                });
            }

            var notification = new HandleUserinfoRequestContext(Context, Options, request, ticket);
            notification.Issuer = Context.GetIssuer(Options);
            notification.Subject = ticket.Principal.GetClaim(OpenIdConnectConstants.Claims.Subject);

            // Note: when receiving an access token, its audiences list cannot be used for the "aud" claim
            // as the client application is not the intented audience but only an authorized presenter.
            // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoResponse
            notification.Audiences.UnionWith(ticket.GetPresenters());

            // The following claims are all optional and should be excluded when
            // no corresponding value has been found in the authentication ticket.
            if (ticket.HasScope(OpenIdConnectConstants.Scopes.Profile))
            {
                notification.FamilyName = ticket.Principal.GetClaim(OpenIdConnectConstants.Claims.FamilyName);
                notification.GivenName = ticket.Principal.GetClaim(OpenIdConnectConstants.Claims.GivenName);
                notification.BirthDate = ticket.Principal.GetClaim(OpenIdConnectConstants.Claims.Birthdate);
            }

            if (ticket.HasScope(OpenIdConnectConstants.Scopes.Email))
            {
                notification.Email = ticket.Principal.GetClaim(OpenIdConnectConstants.Claims.Email);
            }

            if (ticket.HasScope(OpenIdConnectConstants.Scopes.Phone))
            {
                notification.PhoneNumber = ticket.Principal.GetClaim(OpenIdConnectConstants.Claims.PhoneNumber);
            }

            await Options.Provider.HandleUserinfoRequest(notification);

            if (notification.HandledResponse)
            {
                Logger.LogDebug("The userinfo request was handled in user code.");

                return true;
            }

            else if (notification.Skipped)
            {
                Logger.LogDebug("The default userinfo request handling was skipped from user code.");

                return false;
            }

            else if (notification.IsRejected)
            {
                Logger.LogError("The userinfo request was rejected with the following error: {Error} ; {Description}",
                                /* Error: */ notification.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                                /* Description: */ notification.ErrorDescription);

                return await SendUserinfoResponseAsync(new OpenIdConnectResponse
                {
                    Error = notification.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = notification.ErrorDescription,
                    ErrorUri = notification.ErrorUri
                });
            }

            // Ensure the "sub" claim has been correctly populated.
            if (string.IsNullOrEmpty(notification.Subject))
            {
                throw new InvalidOperationException("The subject claim cannot be null or empty.");
            }

            var response = new OpenIdConnectResponse
            {
                [OpenIdConnectConstants.Claims.Subject] = notification.Subject,
                [OpenIdConnectConstants.Claims.Address] = notification.Address,
                [OpenIdConnectConstants.Claims.Birthdate] = notification.BirthDate,
                [OpenIdConnectConstants.Claims.Email] = notification.Email,
                [OpenIdConnectConstants.Claims.EmailVerified] = notification.EmailVerified,
                [OpenIdConnectConstants.Claims.FamilyName] = notification.FamilyName,
                [OpenIdConnectConstants.Claims.GivenName] = notification.GivenName,
                [OpenIdConnectConstants.Claims.Issuer] = notification.Issuer,
                [OpenIdConnectConstants.Claims.PhoneNumber] = notification.PhoneNumber,
                [OpenIdConnectConstants.Claims.PhoneNumberVerified] = notification.PhoneNumberVerified,
                [OpenIdConnectConstants.Claims.PreferredUsername] = notification.PreferredUsername,
                [OpenIdConnectConstants.Claims.Profile] = notification.Profile,
                [OpenIdConnectConstants.Claims.Website] = notification.Website
            };

            switch (notification.Audiences.Count)
            {
                case 0: break;

                case 1:
                    response[OpenIdConnectConstants.Claims.Audience] = notification.Audiences.ElementAt(0);
                    break;

                default:
                    response[OpenIdConnectConstants.Claims.Audience] = new JArray(notification.Audiences);
                    break;
            }

            foreach (var claim in notification.Claims)
            {
                response.SetParameter(claim.Key, claim.Value);
            }

            return await SendUserinfoResponseAsync(response);
        }

        private async Task<bool> SendUserinfoResponseAsync(OpenIdConnectResponse response)
        {
            var request = Context.GetOpenIdConnectRequest();
            Context.SetOpenIdConnectResponse(response);

            response.SetProperty(OpenIdConnectConstants.Properties.MessageType,
                                 OpenIdConnectConstants.MessageTypes.UserinfoResponse);

            var notification = new ApplyUserinfoResponseContext(Context, Options, request, response);
            await Options.Provider.ApplyUserinfoResponse(notification);

            if (notification.HandledResponse)
            {
                Logger.LogDebug("The userinfo request was handled in user code.");

                return true;
            }

            else if (notification.Skipped)
            {
                Logger.LogDebug("The default userinfo request handling was skipped from user code.");

                return false;
            }

            Logger.LogInformation("The userinfo response was successfully returned: {Response}", response);

            return await SendPayloadAsync(response);
        }
    }
}
