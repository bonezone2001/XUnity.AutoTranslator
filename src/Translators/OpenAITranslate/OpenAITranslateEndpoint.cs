using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using SimpleJSON;
using XUnity.AutoTranslator.Plugin.Core.Configuration;
using XUnity.AutoTranslator.Plugin.Core.Constants;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using XUnity.AutoTranslator.Plugin.Core.Endpoints.Http;
using XUnity.AutoTranslator.Plugin.Core.Extensions;
using XUnity.AutoTranslator.Plugin.Core.Utilities;
using XUnity.AutoTranslator.Plugin.Core.Web;
using XUnity.Common.Logging;

namespace OpenAITranslate
{
   internal class OpenAITranslateEndpoint : HttpEndpoint
   {
      private const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";
      private const string DefaultModel = "gpt-4o-mini";
      private const float DefaultTemperature = 0.3f;
      private const int DefaultMaxTokens = 500;

      private string _endpoint;
      private string _apiKey;
      private string _model;
      private float _temperature;
      private int _maxTokens;
      private string _systemPrompt;
      private string _userPromptTemplate;
      private bool _disableSpamChecks;
      private float _minDelay;
      private float _maxDelay;
      private bool _isLocalEndpoint;
      private string _friendlyName;

      public OpenAITranslateEndpoint()
      {
         _friendlyName = "OpenAI";
      }

      public override string Id => "OpenAI";

      public override string FriendlyName => _friendlyName;

      public override int MaxTranslationsPerRequest => 1;

      private string FixLanguage(string lang)
      {
         // Map common language codes to full names for better results
         switch (lang?.ToLowerInvariant())
         {
            case "en":
            case "en-us":
            case "en-gb":
               return "English";
            case "ja":
               return "Japanese";
            case "zh":
            case "zh-cn":
            case "zh-hans":
               return "Simplified Chinese";
            case "zh-tw":
            case "zh-hant":
               return "Traditional Chinese";
            case "ko":
               return "Korean";
            case "es":
               return "Spanish";
            case "fr":
               return "French";
            case "de":
               return "German";
            case "it":
               return "Italian";
            case "pt":
            case "pt-br":
               return "Portuguese";
            case "ru":
               return "Russian";
            case "ar":
               return "Arabic";
            case "th":
               return "Thai";
            case "vi":
               return "Vietnamese";
            case "id":
               return "Indonesian";
            case "nl":
               return "Dutch";
            case "pl":
               return "Polish";
            case "tr":
               return "Turkish";
            default:
               return lang;
         }
      }

      public override void Initialize(IInitializationContext context)
      {
         // Read configuration
         _endpoint = context.GetOrCreateSetting("OpenAI", "Endpoint", DefaultEndpoint);
         _apiKey = context.GetOrCreateSetting("OpenAI", "ApiKey", "");
         _model = context.GetOrCreateSetting("OpenAI", "Model", DefaultModel);
         _temperature = context.GetOrCreateSetting("OpenAI", "Temperature", DefaultTemperature);
         _maxTokens = context.GetOrCreateSetting("OpenAI", "MaxTokens", DefaultMaxTokens);
         
         // System prompt configuration
         _systemPrompt = context.GetOrCreateSetting("OpenAI", "SystemPrompt", 
            "You are a professional translator. Translate the given text accurately while preserving the original meaning, tone, and style. " +
            "Only respond with the translated text, nothing else.");
         
         // User prompt template - {0} = source language, {1} = destination language, {2} = text to translate
         // Don't use GetOrCreateSetting here because it will save the formatted result back to config
         _userPromptTemplate = "Translate the following text from {0} to {1}:\n\n{2}";

         // Spam prevention and delay settings
         _disableSpamChecks = context.GetOrCreateSetting("OpenAI", "DisableSpamChecks", false);
         _minDelay = context.GetOrCreateSetting("OpenAI", "MinDelaySeconds", 1.0f);
         _maxDelay = context.GetOrCreateSetting("OpenAI", "MaxDelaySeconds", 2.0f);

         // Determine if this is a local endpoint (localhost or 127.0.0.1 or common local ports)
         var uri = new Uri(_endpoint);
         _isLocalEndpoint = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                           uri.Host.Equals("127.0.0.1") ||
                           uri.Host.StartsWith("192.168.") ||
                           uri.Host.StartsWith("10.") ||
                           uri.Host.StartsWith("172.16.") ||
                           uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

         // Auto-enable features for local endpoints
         if (_isLocalEndpoint)
         {
            XuaLogger.AutoTranslator.Info("[OpenAI] Detected local endpoint. Auto-enabling optimizations for local usage.");
            _disableSpamChecks = true;
            _minDelay = 0.1f;
            _maxDelay = 0.2f;
         }

         // Validate API key for non-local endpoints
         if (!_isLocalEndpoint && string.IsNullOrEmpty(_apiKey))
         {
            XuaLogger.AutoTranslator.Warn("[OpenAI] No API key configured. This may cause authentication errors with the OpenAI API.");
         }

         // Disable certificate checks for the endpoint
         context.DisableCertificateChecksFor(uri.Host);

         // Update friendly name to show which endpoint is being used
         if (_isLocalEndpoint)
         {
            _friendlyName = $"OpenAI (Local: {uri.Host}:{uri.Port})";
         }
         else if (_endpoint != DefaultEndpoint)
         {
            _friendlyName = $"OpenAI (Custom: {uri.Host})";
         }
         else
         {
            _friendlyName = $"OpenAI ({_model})";
         }

         // Configure spam checks and delays
         if (_disableSpamChecks)
         {
            context.DisableSpamChecks();
         }

         if (_maxDelay > 0)
         {
            context.SetTranslationDelay(_maxDelay);
         }

         XuaLogger.AutoTranslator.Info($"[OpenAI] Initialized with endpoint: {_endpoint}");
         XuaLogger.AutoTranslator.Info($"[OpenAI] Model: {_model}, Temperature: {_temperature}, MaxTokens: {_maxTokens}");
         XuaLogger.AutoTranslator.Info($"[OpenAI] Spam checks: {(_disableSpamChecks ? "Disabled" : "Enabled")}, Delay: {_minDelay}s-{_maxDelay}s");
      }

      public override void OnCreateRequest(IHttpRequestCreationContext context)
      {
         // Prepare the prompt
         var sourceLanguage = FixLanguage(context.SourceLanguage);
         var destinationLanguage = FixLanguage(context.DestinationLanguage);
         var userPrompt = string.Format(_userPromptTemplate, sourceLanguage, destinationLanguage, context.UntranslatedText);

         // Build the JSON request body according to OpenAI API spec
         var requestBody = new JSONObject();
         requestBody["model"] = _model;
         requestBody["temperature"] = _temperature;
         requestBody["max_tokens"] = _maxTokens;

         var messages = new JSONArray();
         
         // System message
         var systemMessage = new JSONObject();
         systemMessage["role"] = "system";
         systemMessage["content"] = _systemPrompt;
         messages.Add(systemMessage);

         // User message
         var userMessage = new JSONObject();
         userMessage["role"] = "user";
         userMessage["content"] = userPrompt;
         messages.Add(userMessage);

         requestBody["messages"] = messages;

         var requestBodyString = requestBody.ToString();

         // Debug log the request being sent
         XuaLogger.AutoTranslator.Debug($"[OpenAI] Request to translate: '{context.UntranslatedText}'");
         XuaLogger.AutoTranslator.Debug($"[OpenAI] Request body: {requestBodyString}");

         // Create the request
         var request = new XUnityWebRequest("POST", _endpoint, requestBodyString);
         
         // Set headers
         request.Headers[HttpRequestHeader.ContentType] = "application/json";
         request.Headers[HttpRequestHeader.Accept] = "application/json";
         
         // Add Authorization header if API key is provided
         if (!string.IsNullOrEmpty(_apiKey))
         {
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + _apiKey;
         }

         // Set User-Agent
         request.Headers[HttpRequestHeader.UserAgent] = UserAgents.Chrome_Win10_Latest;

         context.Complete(request);
      }

      public override void OnExtractTranslation(IHttpTranslationExtractionContext context)
      {
         try
         {
            var response = context.Response;
            
            // Check for errors
            if (response.Error != null)
            {
               context.Fail("OpenAI API request failed: " + response.Error.Message);
               return;
            }

            // Parse the JSON response
            var json = JSON.Parse(response.Data);

            // Debug log the response
            XuaLogger.AutoTranslator.Debug($"[OpenAI] Response: {response.Data}");

            // Check for API errors
            if (json["error"] != null)
            {
               var errorMessage = json["error"]["message"]?.Value ?? "Unknown error";
               context.Fail("OpenAI API error: " + errorMessage);
               return;
            }

            // Extract the translated text from the response
            // Response format: { "choices": [{ "message": { "content": "translated text" } }] }
            var choices = json["choices"];
            if (choices == null || choices.Count == 0)
            {
               context.Fail("OpenAI API returned no choices");
               return;
            }

            var firstChoice = choices[0];
            var message = firstChoice["message"];
            if (message == null)
            {
               context.Fail("OpenAI API response missing message");
               return;
            }

            // Try to get content first, then fall back to reasoning field
            var translatedText = message["content"]?.Value;
            
            // Some models (especially reasoning models) may put the actual response in a "reasoning" field
            if (string.IsNullOrEmpty(translatedText))
            {
               translatedText = message["reasoning"]?.Value;
            }
            
            if (string.IsNullOrEmpty(translatedText))
            {
               context.Fail("OpenAI API returned empty translation (both content and reasoning fields are empty)");
               return;
            }

            // Trim any whitespace from the response
            translatedText = translatedText.Trim();

            context.Complete(translatedText);
         }
         catch (Exception ex)
         {
            context.Fail("Failed to parse OpenAI response: " + ex.Message);
         }
      }
   }
}
