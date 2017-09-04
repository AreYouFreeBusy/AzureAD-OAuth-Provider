﻿//  Copyright 2014 Stefan Negritoiu. See LICENSE file for more information.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Owin;
using Microsoft.Owin.Infrastructure;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Owin.Security.Providers.AzureAD
{
    public class AzureADAuthenticationHandler : AuthenticationHandler<AzureADAuthenticationOptions>
    {
        // for endpoint docs see 
        // https://docs.microsoft.com/en-us/azure/active-directory/develop/active-directory-protocols-oauth-code 
        private const string AuthorizeEndpointFormat = "https://login.microsoftonline.com/{0}/oauth2/authorize";
        private const string TokenEndpointFormat = "https://login.microsoftonline.com/{0}/oauth2/token";
        private const string XmlSchemaString = "http://www.w3.org/2001/XMLSchema#string";
        private const string UserInfoEndpoint = "https://outlook.office.com/api/v2.0/me";

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public AzureADAuthenticationHandler(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        protected override async Task<AuthenticationTicket> AuthenticateCoreAsync()
        {
            AuthenticationProperties properties = null;

            try
            {
                string code = null;
                string state = null;

                IReadableStringCollection query = Request.Query;
                IList<string> values = query.GetValues("code");
                if (values != null && values.Count == 1)
                {
                    code = values[0];
                }
                values = query.GetValues("state");
                if (values != null && values.Count == 1) 
                {
                    state = values[0];
                }

                properties = Options.StateDataFormat.Unprotect(state);
                if (properties == null) {
                    return null;
                }

                // OAuth2 10.12 CSRF
                if (!ValidateCorrelationId(properties, _logger))
                {
                    return new AuthenticationTicket(null, properties);
                }

                string requestPrefix = Request.Scheme + "://" + Request.Host;
                string redirectUri = requestPrefix + Request.PathBase + Options.CallbackPath;

                // Build up the body for the token request
                var body = new List<KeyValuePair<string, string>>();
                body.Add(new KeyValuePair<string, string>("grant_type", "authorization_code"));
                body.Add(new KeyValuePair<string, string>("code", code));
                body.Add(new KeyValuePair<string, string>("redirect_uri", redirectUri));
                body.Add(new KeyValuePair<string, string>("client_id", Options.ClientId));
                body.Add(new KeyValuePair<string, string>("client_secret", Options.ClientSecret));

                // Request the token
                var httpRequest =
                    new HttpRequestMessage(HttpMethod.Post, String.Format(TokenEndpointFormat, Options.Tenant));
                httpRequest.Content = new FormUrlEncodedContent(body);
                if (Options.RequestLogging) 
                {
                    _logger.WriteInformation(httpRequest.ToLogString());
                }
                var httpResponse = await _httpClient.SendAsync(httpRequest);
                httpResponse.EnsureSuccessStatusCode();
                string text = await httpResponse.Content.ReadAsStringAsync();
                if (Options.ResponseLogging) 
                {
                    // Note: avoid using one of the Write* methods that takes a format string as input
                    // because the curly brackets from a JSON response will be interpreted as
                    // curly brackets for the format string and function will throw a FormatException
                    _logger.WriteInformation(httpResponse.ToLogString());
                }
                // Deserializes the token response
                JObject response = JsonConvert.DeserializeObject<JObject>(text);
                string accessToken = response.Value<string>("access_token");
                string scope = response.Value<string>("scope");
                string expires = response.Value<string>("expires_in");
                string refreshToken = response.Value<string>("refresh_token");
                string pwdexpires = response.Value<string>("pwd_exp");
                string pwdchange = response.Value<string>("pwd_url");
                string idToken = response.Value<string>("id_token");

                // get user info
                string userDisplayName = null;
                string userEmail = null;
                var userRequest = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
                userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var userResponse = await _httpClient.SendAsync(userRequest);
                var userContent = await userResponse.Content.ReadAsStringAsync();
                if (userResponse.IsSuccessStatusCode) 
                {
                    var userJson = JObject.Parse(userContent);
                    userDisplayName = userJson["DisplayName"]?.Value<string>();
                    userEmail = userJson["EmailAddress"]?.Value<string>();
                }

                // id_token should be a Base64 url encoded JSON web token
                JObject id = null;
                string[] segments;
                if (!String.IsNullOrEmpty(idToken) && (segments = idToken.Split('.')).Length == 3) {
                    string payload = base64urldecode(segments[1]);
                    if (!String.IsNullOrEmpty(payload)) id = JObject.Parse(payload);
                }

                var context = new AzureADAuthenticatedContext(Context, id, accessToken, scope, expires, refreshToken, pwdexpires, pwdchange);
                context.Identity = new ClaimsIdentity(
                    Options.AuthenticationType,
                    ClaimsIdentity.DefaultNameClaimType,
                    ClaimsIdentity.DefaultRoleClaimType);

                if (!string.IsNullOrEmpty(context.ObjectId)) 
                {
                    context.Identity.AddClaim(
                        new Claim(ClaimTypes.NameIdentifier, context.ObjectId, XmlSchemaString, Options.AuthenticationType));
                }
                if (!string.IsNullOrEmpty(context.Upn)) 
                {
                    context.Identity.AddClaim(
                        new Claim(ClaimTypes.Upn, context.Upn, XmlSchemaString, Options.AuthenticationType));
                }
                if (!string.IsNullOrEmpty(userEmail)) 
                {
                    context.Email = userEmail;
                    context.Identity.AddClaim(
                        new Claim(ClaimTypes.Email, userEmail, XmlSchemaString, Options.AuthenticationType));
                }
                if (!string.IsNullOrEmpty(context.GivenName)) 
                {
                    context.Identity.AddClaim(
                        new Claim(ClaimTypes.GivenName, context.GivenName, XmlSchemaString, Options.AuthenticationType));
                }
                if (!string.IsNullOrEmpty(context.FamilyName)) 
                {
                    context.Identity.AddClaim(
                        new Claim(ClaimTypes.Surname, context.FamilyName, XmlSchemaString, Options.AuthenticationType));
                }
                if (!string.IsNullOrEmpty(userDisplayName) || !string.IsNullOrEmpty(context.Name)) 
                {
                    context.Identity.AddClaim(
                        new Claim(ClaimsIdentity.DefaultNameClaimType, userDisplayName ?? context.Name, XmlSchemaString, Options.AuthenticationType));
                }

                context.Properties = properties;

                await Options.Provider.Authenticated(context);

                return new AuthenticationTicket(context.Identity, context.Properties);
            }
            catch (Exception ex)
            {
                _logger.WriteError("Authentication failed", ex);
                return new AuthenticationTicket(null, properties);
            }
        }

        protected override Task ApplyResponseChallengeAsync()
        {
            if (Response.StatusCode != 401)
            {
                return Task.FromResult<object>(null);
            }

            AuthenticationResponseChallenge challenge = 
                Helper.LookupChallenge(Options.AuthenticationType, Options.AuthenticationMode);

            if (challenge != null)
            {
                string baseUri =
                    Request.Scheme +
                    Uri.SchemeDelimiter +
                    Request.Host +
                    Request.PathBase;

                string currentUri =
                    baseUri +
                    Request.Path +
                    Request.QueryString;

                string redirectUri =
                    baseUri +
                    Options.CallbackPath;

                AuthenticationProperties properties = challenge.Properties;
                if (string.IsNullOrEmpty(properties.RedirectUri))
                {
                    properties.RedirectUri = currentUri;
                }

                // OAuth2 10.12 CSRF
                GenerateCorrelationId(properties);

                var queryStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                queryStrings.Add("response_type", "code");
                queryStrings.Add("client_id", Options.ClientId);
                queryStrings.Add("redirect_uri", redirectUri);

                // AzureAD requires at least one resource. If none provided, set default to the AD Graph API.
                if (Options.Resource.Count == 0) Options.Resource.Add("https://graph.windows.net");

                // concatenate with spaces for now, like scope, although this option isn't clearly documented
                string resource = string.Join(" ", Options.Resource);

                AddQueryString(queryStrings, properties, "resource", resource);
                AddQueryString(queryStrings, properties, "prompt");
                AddQueryString(queryStrings, properties, "login_hint");
                AddQueryString(queryStrings, properties, "domain_hint");
                // Microsoft-specific parameter
                // msafed=0 forces the interpretation of login_hint as an organizational accoount
                // and does not present to user the Work vs. Personal account picker
                AddQueryString(queryStrings, properties, "msafed");

                string state = Options.StateDataFormat.Protect(properties);
                queryStrings.Add("state", state);
                queryStrings.Add("nonce", state);

                string authorizationEndpoint =
                    WebUtilities.AddQueryString(String.Format(AuthorizeEndpointFormat, Options.Tenant), queryStrings);

                if (Options.RequestLogging) 
                {
                    _logger.WriteInformation(String.Format("GET {0}", authorizationEndpoint));
                }
                Response.Redirect(authorizationEndpoint);
            }

            return Task.FromResult<object>(null);
        }

        public override async Task<bool> InvokeAsync()
        {
            return await InvokeReplyPathAsync();
        }

        private async Task<bool> InvokeReplyPathAsync()
        {
            if (Options.CallbackPath.HasValue && Options.CallbackPath == Request.Path)
            {
                // TODO: error responses

                AuthenticationTicket ticket = await AuthenticateAsync();
                if (ticket == null)
                {
                    _logger.WriteWarning("Invalid return state, unable to redirect.");
                    Response.StatusCode = 500;
                    return true;
                }

                var context = new AzureADReturnEndpointContext(Context, ticket);
                context.SignInAsAuthenticationType = Options.SignInAsAuthenticationType;
                context.RedirectUri = ticket.Properties.RedirectUri;

                await Options.Provider.ReturnEndpoint(context);

                if (context.SignInAsAuthenticationType != null && context.Identity != null)
                {
                    ClaimsIdentity grantIdentity = context.Identity;
                    if (!string.Equals(grantIdentity.AuthenticationType, context.SignInAsAuthenticationType, StringComparison.Ordinal))
                    {
                        grantIdentity = new ClaimsIdentity(
                            grantIdentity.Claims, 
                            context.SignInAsAuthenticationType, 
                            grantIdentity.NameClaimType, 
                            grantIdentity.RoleClaimType);
                    }
                    Context.Authentication.SignIn(context.Properties, grantIdentity);
                }

                if (!context.IsRequestCompleted && context.RedirectUri != null)
                {
                    string redirectUri = context.RedirectUri;
                    if (context.Identity == null)
                    {
                        // add a redirect hint that sign-in failed in some way
                        redirectUri = WebUtilities.AddQueryString(redirectUri, "error", "internal");
                    }
                    Response.Redirect(redirectUri);
                    context.RequestCompleted();
                }

                return context.IsRequestCompleted;
            }
            return false;
        }

        private static void AddQueryString(IDictionary<string, string> queryStrings, AuthenticationProperties properties,
            string name, string defaultValue = null) 
        {
            string value;
            if (!properties.Dictionary.TryGetValue(name, out value)) 
            {
                value = defaultValue;
            }
            else 
            {
                // Remove the parameter from AuthenticationProperties so it won't be serialized to state parameter
                properties.Dictionary.Remove(name);
            }

            if (value == null) 
            {
                return;
            }

            queryStrings[name] = value;
        }

        /// <summary>
        /// Based on http://tools.ietf.org/html/draft-ietf-jose-json-web-signature-08#appendix-C
        /// </summary>
        static string base64urldecode(string arg) 
        {
            string s = arg;
            s = s.Replace('-', '+'); // 62nd char of encoding
            s = s.Replace('_', '/'); // 63rd char of encoding
            switch (s.Length % 4) // Pad with trailing '='s
            {
                case 0: break; // No pad chars in this case
                case 2: s += "=="; break; // Two pad chars
                case 3: s += "="; break; // One pad char
                default: throw new System.Exception("Illegal base64url string!");
            }

            try 
            {
                System.Text.UTF8Encoding encoding = new System.Text.UTF8Encoding();                
                return encoding.GetString(Convert.FromBase64String(s)); // Standard base64 decoder
            }
            catch (FormatException) 
            {
                return null;
            }
        }
    }


    public static class AzureADAuthenticationHandlerExtensions 
    {
        /// <summary>
        /// 
        /// </summary>
        public static string ToLogString(this HttpRequestMessage httpRequest) 
        {
            var serializedRequest = AsyncHelpers.RunSync<byte[]>(() =>
                new HttpMessageContent(httpRequest).ReadAsByteArrayAsync());
            return System.Text.UTF8Encoding.UTF8.GetString(serializedRequest);
        }


        /// <summary>
        /// 
        /// </summary>
        public static string ToLogString(this HttpResponseMessage httpResponse) 
        {
            var serializedRequest = AsyncHelpers.RunSync<byte[]>(() =>
                new HttpMessageContent(httpResponse).ReadAsByteArrayAsync());
            return System.Text.UTF8Encoding.UTF8.GetString(serializedRequest);
        } 
    }
}