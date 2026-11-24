#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>MSI property bag shared by the wizard, the immediate custom actions, and the tests.</summary>
    public interface IInstallerProperties
    {
        string Get(string name);

        void Set(string name, string value);
    }

    /// <summary>Property bag for handoff tests and the packaged custom-action smoke check.</summary>
    public sealed class DictionaryInstallerProperties : IInstallerProperties
    {
        public DictionaryInstallerProperties()
            : this(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
        }

        public DictionaryInstallerProperties(IDictionary<string, string> values)
        {
            Values = values ?? throw new ArgumentNullException(nameof(values));
        }

        public IDictionary<string, string> Values { get; }

        public string Get(string name)
        {
            string? value;
            return Values.TryGetValue(name, out value) ? value ?? string.Empty : string.Empty;
        }

        public void Set(string name, string value)
        {
            Values[name] = value ?? string.Empty;
        }
    }

    /// <summary>
    ///     Carries listen address and port intent from the unelevated wizard to the elevated settings write.
    ///     Blank, non-explicit fields are resolved from the settings file after elevation. Authentication and volume-root
    ///     behavior stay on their existing properties.
    /// </summary>
    public static class SettingsHandoff
    {
        public const string ListenModeProperty = "VESTIGO_LISTEN_MODE";
        public const string ListenModeExplicitProperty = "VESTIGO_LISTEN_MODE_EXPLICIT";
        public const string ListenUiModeProperty = "VESTIGO_LISTEN_UI_MODE";
        public const string ListenModePrefillProperty = "VESTIGO_LISTEN_MODE_PREFILL";
        public const string AuthModeProperty = "VESTIGO_AUTH_MODE";
        public const string PortProperty = "VESTIGO_PORT";
        public const string PortExplicitProperty = "VESTIGO_PORT_EXPLICIT";
        public const string PortPrefillProperty = "VESTIGO_PORT_PREFILL";
        public const string TokenProperty = "VESTIGO_API_TOKEN";
        public const string RootsModeProperty = "VESTIGO_ROOTS_MODE";
        public const string RootsAccessProperty = "VESTIGO_ROOTS_ACCESS";
        public const string RootsAccessPrefillProperty = "VESTIGO_ROOTS_ACCESS_PREFILL";
        public const string RootsAccessExplicitProperty = "VESTIGO_ROOTS_ACCESS_EXPLICIT";
        public const string ErrorProperty = "VESTIGO_INPUT_ERROR";
        public const string SuggestedProperty = "VESTIGO_TOKEN_SUGGESTED";
        public const string ProtectedProperty = "VESTIGO_SETTINGS_PROTECTED";
        public const string AuthUiModeProperty = "VESTIGO_AUTH_UI_MODE";
        public const string ValidateTokenProperty = "VESTIGO_VALIDATE_TOKEN";
        public const string LoadInitializedProperty = "VESTIGO_LOAD_INITIALIZED";

        public const string DataListenMode = "ListenMode";
        public const string DataListenModeExplicit = "ListenModeExplicit";
        public const string DataAuthMode = "AuthMode";
        public const string DataPort = "Port";
        public const string DataPortExplicit = "PortExplicit";
        public const string DataToken = "Token";
        public const string DataTokenIsSuggestion = "TokenIsSuggestion";
        public const string DataReplaceApiTokens = "ReplaceApiTokens";
        public const string DataRootsMode = "RootsMode";
        public const string DataRootsAccess = "RootsAccess";
        public const string DataRootsAccessExplicit = "RootsAccessExplicit";
        public const string DataModules = "Modules";
        public const string DataIsUpgrade = "IsUpgrade";
        public const string DataIsInstalled = "IsInstalled";

        public static readonly string[] WriteDataKeys =
        {
            DataListenMode,
            DataListenModeExplicit,
            DataAuthMode,
            DataPort,
            DataPortExplicit,
            DataToken,
            DataTokenIsSuggestion,
            DataReplaceApiTokens,
            DataRootsMode,
            DataRootsAccess,
            DataRootsAccessExplicit,
            DataModules,
            DataIsUpgrade,
            DataIsInstalled
        };

        public static void Load(
            IInstallerProperties session,
            string? settingsJson,
            bool settingsProtected,
            bool isUpgrade,
            bool isInstalled,
            Action<string>? log)
        {
            if (settingsProtected)
            {
                session.Set(ProtectedProperty, "1");
            }

            if (session.Get(LoadInitializedProperty) != "1")
            {
                string? listenArgument = BlankToNull(session.Get(ListenModeProperty));
                string? portArgument = BlankToNull(session.Get(PortProperty));
                if (listenArgument != null || portArgument != null)
                {
                    ResolvedSettings checkedValues = ListenSettings.Resolve(
                        listenArgument,
                        ListenSettings.AuthNone,
                        portArgument,
                        null,
                        null);
                    if (listenArgument != null)
                    {
                        session.Set(ListenModeProperty, checkedValues.ListenMode);
                    }

                    if (portArgument != null)
                    {
                        session.Set(PortProperty, checkedValues.Port.ToString(CultureInfo.InvariantCulture));
                    }
                }

                session.Set(ListenModeExplicitProperty, listenArgument != null ? "1" : "0");
                session.Set(PortExplicitProperty, portArgument != null ? "1" : "0");
                session.Set(LoadInitializedProperty, "1");
            }

            ExistingSettings? existing = settingsProtected ? null : ListenSettings.ReadExisting(settingsJson);
            string requestedToken = session.Get(TokenProperty);
            bool hadToken = !IsBlank(requestedToken) || (existing != null && existing.Tokens.Count > 0);
            bool tokenIsSuggestion = !IsBlank(session.Get(SuggestedProperty)) &&
                                      string.Equals(requestedToken, session.Get(SuggestedProperty), StringComparison.Ordinal);
            string authMode = session.Get(AuthModeProperty);
            bool preserveMissing = isUpgrade || isInstalled;

            if (settingsProtected)
            {
                ApplyProtectedAuthLoad(session, ref requestedToken, ref authMode, ref tokenIsSuggestion, isUpgrade);
                if (IsBlank(session.Get(ListenUiModeProperty)))
                {
                    session.Set(
                        ListenUiModeProperty,
                        ListenSettings.ResolveProtectedUiListenMode(
                            session.Get(ListenModeExplicitProperty) == "1" ? session.Get(ListenModeProperty) : null,
                            session.Get(ListenModeExplicitProperty) == "1"));
                }
            }

            bool listenExplicit = session.Get(ListenModeExplicitProperty) == "1";
            bool portExplicit = session.Get(PortExplicitProperty) == "1";
            ResolvedSettings resolved = ListenSettings.Resolve(
                listenExplicit ? BlankToNull(session.Get(ListenModeProperty)) : null,
                authMode,
                portExplicit ? BlankToNull(session.Get(PortProperty)) : null,
                requestedToken,
                existing,
                tokenIsSuggestion,
                preserveMissing);
            session.Set(AuthModeProperty, resolved.AuthMode);
            session.Set(TokenProperty, resolved.Token);
            if (settingsProtected)
            {
                session.Set(ListenModeProperty, listenExplicit ? resolved.ListenMode : string.Empty);
                session.Set(PortProperty, portExplicit ? resolved.Port.ToString(CultureInfo.InvariantCulture) : string.Empty);
            }
            else
            {
                if (!listenExplicit)
                {
                    session.Set(ListenModeProperty, resolved.ListenMode);
                    if (IsBlank(session.Get(ListenModePrefillProperty)))
                    {
                        session.Set(ListenModePrefillProperty, resolved.ListenMode);
                    }
                }

                if (!portExplicit)
                {
                    string port = resolved.Port.ToString(CultureInfo.InvariantCulture);
                    session.Set(PortProperty, port);
                    if (IsBlank(session.Get(PortPrefillProperty)))
                    {
                        session.Set(PortPrefillProperty, port);
                    }
                }
            }

            if (!settingsProtected && !hadToken && resolved.AuthMode == ListenSettings.AuthToken)
            {
                session.Set(SuggestedProperty, resolved.Token);
            }

            if (log != null)
            {
                log(Describe(session));
            }
        }

        public static void Apply(
            IInstallerProperties session,
            string? settingsJson,
            bool settingsProtected,
            bool isUpgrade,
            bool isInstalled)
        {
            session.Set(ErrorProperty, string.Empty);
            try
            {
                bool protectedUi = settingsProtected || session.Get(ProtectedProperty) == "1";
                ExistingSettings? existing = protectedUi ? null : ListenSettings.ReadExisting(settingsJson);
                string token = session.Get(TokenProperty);
                bool suggestion = !IsBlank(token) &&
                                  string.Equals(token, session.Get(SuggestedProperty), StringComparison.Ordinal);
                string authMode = session.Get(AuthModeProperty);
                if (protectedUi)
                {
                    ApplyProtectedListen(session);
                    ApplyProtectedAuthChoice(session, ref token, ref authMode, ref suggestion);
                }
                else
                {
                    NoteEditableListen(session);
                    if (!IsBlank(token))
                    {
                        token = token.Trim();
                        session.Set(TokenProperty, token);
                        string? tokenError = ListenSettings.ValidateToken(token);
                        if (authMode.Trim().Equals(ListenSettings.AuthToken, StringComparison.OrdinalIgnoreCase) && tokenError != null)
                        {
                            throw new ArgumentException(tokenError);
                        }
                    }
                }

                bool listenExplicit = session.Get(ListenModeExplicitProperty) == "1";
                bool portExplicit = session.Get(PortExplicitProperty) == "1";
                ResolvedSettings resolved = ListenSettings.Resolve(
                    listenExplicit ? BlankToNull(session.Get(ListenModeProperty)) : null,
                    authMode,
                    portExplicit ? BlankToNull(session.Get(PortProperty)) : null,
                    token,
                    existing,
                    suggestion,
                    isUpgrade || isInstalled);
                session.Set(AuthModeProperty, resolved.AuthMode);
                session.Set(TokenProperty, resolved.Token);
                if (protectedUi)
                {
                    session.Set(ListenModeProperty, listenExplicit ? resolved.ListenMode : string.Empty);
                    session.Set(PortProperty, portExplicit ? resolved.Port.ToString(CultureInfo.InvariantCulture) : string.Empty);
                }
                else
                {
                    session.Set(ListenModeProperty, resolved.ListenMode);
                    session.Set(PortProperty, resolved.Port.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (ArgumentException ex)
            {
                session.Set(ErrorProperty, ex.Message);
            }
        }

        public static Dictionary<string, string> Prepare(
            IInstallerProperties session,
            bool isUpgrade,
            bool isInstalled,
            string? modulesSerialized)
        {
            Dictionary<string, string> data = new Dictionary<string, string>(StringComparer.Ordinal);
            bool listenExplicit = session.Get(ListenModeExplicitProperty) == "1";
            bool portExplicit = session.Get(PortExplicitProperty) == "1";
            data[DataListenMode] = listenExplicit ? session.Get(ListenModeProperty) : string.Empty;
            data[DataListenModeExplicit] = listenExplicit ? "1" : "0";
            data[DataAuthMode] = session.Get(AuthModeProperty);
            data[DataPort] = portExplicit ? session.Get(PortProperty) : string.Empty;
            data[DataPortExplicit] = portExplicit ? "1" : "0";
            if (string.Equals(session.Get(AuthModeProperty), ListenSettings.AuthNone, StringComparison.OrdinalIgnoreCase))
            {
                data[DataToken] = string.Empty;
                data[DataTokenIsSuggestion] = "0";
            }
            else
            {
                data[DataToken] = session.Get(TokenProperty);
                bool suggestion = !IsBlank(session.Get(SuggestedProperty)) &&
                                  string.Equals(session.Get(TokenProperty), session.Get(SuggestedProperty), StringComparison.Ordinal);
                data[DataTokenIsSuggestion] = suggestion ? "1" : "0";
            }

            data[DataReplaceApiTokens] = session.Get(ProtectedProperty) == "1" &&
                                          string.Equals(session.Get(AuthUiModeProperty), ListenSettings.UiAuthReplace, StringComparison.OrdinalIgnoreCase)
                ? "1"
                : "0";
            data[DataRootsMode] = session.Get(RootsModeProperty);
            data[DataRootsAccess] = session.Get(RootsAccessProperty);
            data[DataRootsAccessExplicit] = AccessIsExplicit(session) ? "1" : "0";
            data[DataModules] = modulesSerialized ?? string.Empty;
            data[DataIsUpgrade] = isUpgrade ? "1" : "0";
            data[DataIsInstalled] = isInstalled ? "1" : "0";
            return data;
        }

        public static ResolvedSettings ResolveWrite(IDictionary<string, string> data, ExistingSettings? existing)
        {
            bool listenExplicit = Value(data, DataListenModeExplicit) == "1";
            bool portExplicit = Value(data, DataPortExplicit) == "1";
            bool preserveMissing = Value(data, DataIsUpgrade) == "1" || Value(data, DataIsInstalled) == "1";
            return ListenSettings.Resolve(
                listenExplicit ? BlankToNull(Value(data, DataListenMode)) : null,
                Value(data, DataAuthMode),
                portExplicit ? BlankToNull(Value(data, DataPort)) : null,
                Value(data, DataToken),
                existing,
                Value(data, DataTokenIsSuggestion) == "1",
                preserveMissing,
                Value(data, DataReplaceApiTokens) == "1");
        }

        public static bool AccessIsExplicit(IInstallerProperties session)
        {
            if (session.Get(RootsAccessExplicitProperty) == "1")
            {
                return true;
            }

            string current = session.Get(RootsAccessProperty);
            string prefill = session.Get(RootsAccessPrefillProperty);
            return !IsBlank(prefill) && !string.Equals(current, prefill, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Installer log line. It names modes and ports and never includes the API token.</summary>
        public static string Describe(IInstallerProperties session)
        {
            string listen = session.Get(ListenModeExplicitProperty) == "1"
                ? Display(session.Get(ListenModeProperty))
                : "(keep)";
            string port = session.Get(PortExplicitProperty) == "1"
                ? Display(session.Get(PortProperty))
                : "(keep)";
            return "Vestigo settings: listen=" + listen +
                   ", auth=" + session.Get(AuthModeProperty) +
                   ", port=" + port +
                   ", roots=" + session.Get(RootsModeProperty) +
                   ", access=" + session.Get(RootsAccessProperty) +
                   ", protected=" + (session.Get(ProtectedProperty) == "1" ? "1" : "0");
        }

        private static void ApplyProtectedListen(IInstallerProperties session)
        {
            ProtectedUiListenChoice choice = ListenSettings.MapProtectedUiListen(session.Get(ListenUiModeProperty));
            if (choice.IsExplicit)
            {
                session.Set(ListenModeProperty, choice.ListenMode);
                session.Set(ListenModeExplicitProperty, "1");
            }
            else
            {
                session.Set(ListenModeProperty, string.Empty);
                session.Set(ListenModeExplicitProperty, "0");
            }

            string port = session.Get(PortProperty);
            if (IsBlank(port))
            {
                session.Set(PortProperty, string.Empty);
                session.Set(PortExplicitProperty, "0");
            }
            else
            {
                session.Set(PortProperty, ListenSettings.ParsePort(port).ToString(CultureInfo.InvariantCulture));
                session.Set(PortExplicitProperty, "1");
            }
        }

        private static void NoteEditableListen(IInstallerProperties session)
        {
            if (session.Get(ListenModeExplicitProperty) != "1")
            {
                string current = session.Get(ListenModeProperty).Trim();
                string prefill = session.Get(ListenModePrefillProperty).Trim();
                if (current.Length > 0 && prefill.Length > 0 &&
                    !string.Equals(current, prefill, StringComparison.OrdinalIgnoreCase))
                {
                    session.Set(ListenModeExplicitProperty, "1");
                }
            }

            if (session.Get(PortExplicitProperty) == "1")
            {
                if (!IsBlank(session.Get(PortProperty)))
                {
                    session.Set(PortProperty, ListenSettings.ParsePort(session.Get(PortProperty)).ToString(CultureInfo.InvariantCulture));
                }

                return;
            }

            if (IsBlank(session.Get(PortProperty)))
            {
                return;
            }

            string parsed = ListenSettings.ParsePort(session.Get(PortProperty)).ToString(CultureInfo.InvariantCulture);
            string portPrefill = session.Get(PortPrefillProperty).Trim();
            session.Set(PortProperty, parsed);
            if (portPrefill.Length > 0 && !string.Equals(parsed, portPrefill, StringComparison.Ordinal))
            {
                session.Set(PortExplicitProperty, "1");
            }
        }

        private static void ApplyProtectedAuthLoad(
            IInstallerProperties session,
            ref string requestedToken,
            ref string authMode,
            ref bool tokenIsSuggestion,
            bool isUpgrade)
        {
            if (IsBlank(session.Get(AuthUiModeProperty)))
            {
                session.Set(
                    AuthUiModeProperty,
                    ListenSettings.ResolveProtectedUiAuthMode(authMode, requestedToken, tokenIsSuggestion, isUpgrade));
            }

            ProtectedUiAuthChoice choice = ListenSettings.MapProtectedUiAuth(session.Get(AuthUiModeProperty));
            authMode = choice.AuthMode;
            tokenIsSuggestion = choice.TokenIsSuggestion;
            if (IsBlank(requestedToken))
            {
                requestedToken = ListenSettings.GenerateToken();
            }

            if (choice.TokenIsSuggestion)
            {
                requestedToken = ListenSettings.SuggestedKeepToken(requestedToken);
                session.Set(SuggestedProperty, requestedToken);
            }
            else
            {
                session.Set(SuggestedProperty, string.Empty);
            }
        }

        private static void ApplyProtectedAuthChoice(
            IInstallerProperties session,
            ref string token,
            ref string authMode,
            ref bool suggestion)
        {
            ProtectedUiAuthChoice choice = ListenSettings.MapProtectedUiAuth(session.Get(AuthUiModeProperty));
            authMode = choice.AuthMode;
            session.Set(AuthModeProperty, authMode);
            if (choice.TokenIsSuggestion)
            {
                token = ListenSettings.SuggestedKeepToken(token);
                session.Set(SuggestedProperty, token);
                session.Set(TokenProperty, token);
                suggestion = true;
                return;
            }

            session.Set(SuggestedProperty, string.Empty);
            suggestion = false;
            if (!choice.RequireTokenValidation)
            {
                return;
            }

            bool enforceToken = session.Get(ValidateTokenProperty) == "1";
            if (IsBlank(token))
            {
                if (enforceToken)
                {
                    throw new ArgumentException("Enter an API token or choose to keep the existing one.");
                }

                token = ListenSettings.GenerateToken();
                session.Set(TokenProperty, token);
                return;
            }

            token = token.Trim();
            session.Set(TokenProperty, token);
            string? tokenError = ListenSettings.ValidateToken(token);
            if (tokenError != null)
            {
                throw new ArgumentException(tokenError);
            }
        }

        private static string Value(IDictionary<string, string> data, string key)
        {
            string? value;
            return data != null && data.TryGetValue(key, out value) ? value ?? string.Empty : string.Empty;
        }

        private static string Display(string value)
        {
            return IsBlank(value) ? "(keep)" : value.Trim();
        }

        private static bool IsBlank(string? value)
        {
            return string.IsNullOrWhiteSpace(value);
        }

        private static string? BlankToNull(string? value)
        {
            return IsBlank(value) ? null : value!.Trim();
        }
    }
}
