﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Seq.Apps;
using Seq.Apps.LogEvents;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

// ReSharper disable MemberCanBePrivate.Global, UnusedType.Global, UnusedAutoPropertyAccessor.Global

namespace Seq.App.Opsgenie
{
    [SeqApp("Opsgenie Alerting Test Build", Description = "Send Opsgenie alerts using the HTTP API.")]
    public class OpsgenieApp : SeqApp, IDisposable, ISubscribeToAsync<LogEventData>
    {
        IDisposable _disposeClient;
        HandlebarsTemplate _generateMessage, _generateDescription;
        string _priority;
        List<Responders> _responders;
        string responder;
        string[] _tags;

        bool _includeTags;
        string _includeTagProperty;


        // Permits substitution for testing.
        internal IOpsgenieApiClient ApiClient { get; set; }

        [SeqAppSetting(
            DisplayName = "API key",
            HelpText = "The Opsgenie API key to use.",
            InputType = SettingInputType.Password)]
        public string ApiKey { get; set; }

        [SeqAppSetting(
            IsOptional = true,
            DisplayName = "Alert message",
            HelpText = "The message associated with the alert, specified with Handlebars syntax. If blank, the message " +
                       "from the incoming event or notification will be used.")]
        public string AlertMessage { get; set; }

        [SeqAppSetting(
            IsOptional = true,
            DisplayName = "Alert description",
            HelpText = "The description associated with the alert, specified with Handlebars syntax. If blank, a default" +
                       " description will be used.")]
        public string AlertDescription { get; set; }

        //TODO - This could also be implemented to allow @Level to Priority mappings
        [SeqAppSetting(
            IsOptional = true,
            DisplayName = "Alert Priority",
            HelpText = "Priority for the alert - P1, P2, P3, P4, P5")]
        public string EventPriority { get; set; }

        //Defaults to team if only name is specified, but we also optionally accept name=type to allow user, escalation, and schedule to be specified
        [SeqAppSetting(
            IsOptional = true,
            DisplayName = "Responders",
            HelpText = "Responders for the alert - team name, or name=[team,user,escalation,schedule] - comma-delimited for multiple")]
        public string Responders { get; set; }

        //Static list of tags
        [SeqAppSetting(
            IsOptional = true,
            DisplayName = "Alert tags",
            HelpText = "Tags for the alert, separated by commas.")]
        public string Tags { get; set; }

        //Optionally allow dynamic tags from an event property
        [SeqAppSetting(
            IsOptional = true,
            DisplayName = "Include Event tags",
            HelpText = "Include tags from from an event property - comma-delimited or array accepted. Will append to existing tags.")]
        public bool AddEventTags { get; set; }

        //The property containing tags that can be added dynamically during runtime
        [SeqAppSetting(
            IsOptional = true,
            DisplayName = "Event tag property",
            HelpText = "The property that contains tags to include from events- defaults to 'Tags', only used if Include Event tags is enabled.")]
        public string AddEventProperty { get; set; }

        protected override void OnAttached()
        {
            _generateMessage = new HandlebarsTemplate(Host, !string.IsNullOrWhiteSpace(AlertMessage) ?
                AlertMessage :
                "{{$Message}}");

            _generateDescription = new HandlebarsTemplate(Host, !string.IsNullOrWhiteSpace(AlertDescription) ?
                AlertDescription :
                $"Generated by Seq running at {Host.BaseUri}.");

            _priority = "P3";
            if (!string.IsNullOrEmpty(EventPriority) && Regex.IsMatch(EventPriority, "(^P[1-5]$)", RegexOptions.IgnoreCase))
                _priority = EventPriority;

            List<Responders> responderList = new List<Responders>();
            if (!string.IsNullOrEmpty(Responders))
                foreach (string responder in Responders.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()))
                    if (responder.Contains("="))
                    {
                        string[] r = responder.Split(new[] { '=' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToArray();
                        ResponderType responderType;
                        if (Enum.TryParse(r[1], true, out responderType))
                            responderList.Add(new Responders() { Name = r[0], Type = responderType });
                        else
                            Log.Debug("Cannot parse responder type: {Responder}", responder);
                    }
                    else
                        responderList.Add(new Responders() { Name = responder, Type = ResponderType.team });

            _responders = responderList;
            responder = JsonSerializer.Serialize(_responders, new JsonSerializerOptions()
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = {
                    new JsonStringEnumConverter()
                }
            });

            _tags = (Tags ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .ToArray();

            _includeTags = AddEventTags;
            _includeTagProperty = "Tags";
            if (!string.IsNullOrEmpty(AddEventProperty))
                _includeTagProperty = AddEventProperty;

            if (ApiClient == null)
            {
                var client = new OpsgenieApiClient(ApiKey);
                ApiClient = client;
                _disposeClient = client;
            }
        }

        public async Task OnAsync(Event<LogEventData> evt)
        {
            if (evt == null) throw new ArgumentNullException(nameof(evt));

            List<string> tagList = _tags.ToList<string>();

            if (_includeTags && evt.Data.Properties.ContainsKey(_includeTagProperty))
                {
                    var property = evt.Data.Properties[_includeTagProperty];
                    if (property.GetType().IsArray)
                        foreach (object p in (object[])property)
                            if (!string.IsNullOrEmpty((string)p) && !tagList.Contains((string)p, StringComparer.CurrentCultureIgnoreCase))
                                tagList.Add(((string)p).Trim());
                            else if (!string.IsNullOrEmpty((string)p) && !tagList.Contains((string)p, StringComparer.CurrentCultureIgnoreCase))
                                tagList.AddRange(((string)property).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()));
                }

            try
            {
                
                //Log our intent to alert OpsGenie with details that could be re-fired to another app if needed
                Log.Debug("Send Alert to OpsGenie: Message {Message}, Description {Description}, Priority {Priority}, Responders {Responders}, Tags {Tags}", _generateMessage.Render(evt), _generateDescription.Render(evt),
                    _priority, responder, tagList.ToArray());

                //Logging the API call helps with debugging "why" an alert did not fire or was rejected by OpsGenie API
                Log.Debug("OpsGenie API call: {JsonCall}", JsonSerializer.Serialize(new OpsgenieAlertWithResponders(
                        _generateMessage.Render(evt),
                        evt.Id,
                        _generateDescription.Render(evt),
                        _priority,
                        _responders,
                        Host.BaseUri,
                        tagList.ToArray()), new JsonSerializerOptions()
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            Converters = {
                                new JsonStringEnumConverter()
                            }
                        }));

                HttpResponseMessage result;
                if (_responders.Count > 0)
                    result = await ApiClient.CreateAsync(new OpsgenieAlertWithResponders(
                        _generateMessage.Render(evt),
                        evt.Id,
                        _generateDescription.Render(evt),
                        _priority,
                        _responders,
                        Host.BaseUri,
                        tagList.ToArray()));
                else
                    result = await ApiClient.CreateAsync(new OpsgenieAlert(
                        _generateMessage.Render(evt),
                        evt.Id,
                        _generateDescription.Render(evt),
                        _priority,
                        Host.BaseUri,
                        tagList.ToArray()));

                //Log the result with details that could be re-fired to another app if needed
                Log.Debug("OpsGenie Result: Result {Result}, Message {Message}, Description {Description}, Priority {Priority}, Responders {Responders}, Tags {Tags}", result.StatusCode, _generateMessage.Render(evt), _generateDescription.Render(evt),
                    _priority, responder, tagList.ToArray());
            }

            catch (Exception ex)
            {
                //Log an error which could be fired to another app (eg. alert via email of an OpsGenie alert failure, or raise a Jira) and include details of the alert
                Log.Error(ex, "OpsGenie Result: Result {Error}, Message {Message}, Description {Description}, Priority {Priority}, Responders {Responders}, Tags {Tags}", ex.Message, _generateMessage.Render(evt), _generateDescription.Render(evt),
                    _priority, responder, _tags.ToArray());
            }
        }

        public void Dispose()
        {
            _disposeClient?.Dispose();
        }
    }
}
