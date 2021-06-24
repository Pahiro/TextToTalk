﻿using Dalamud.CrystalTower.Commands;
using Dalamud.CrystalTower.DependencyInjection;
using Dalamud.CrystalTower.UI;
using Dalamud.Game.Internal;
using Dalamud.Game.Internal.Gui.Addon;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using TextToTalk.Modules;
using TextToTalk.Talk;
using TextToTalk.UI;

namespace TextToTalk
{
    public class TextToTalk : IDalamudPlugin
    {
        //Global Objects
        public static Amazon.Polly.Model.DescribeVoicesResponse Voices;
        public static Amazon.Runtime.BasicAWSCredentials AWSCredentials;
        public static Amazon.Polly.AmazonPollyClient PollyClient;
        public static NAudio.Wave.WaveOut wout = new NAudio.Wave.WaveOut();

        private DalamudPluginInterface pluginInterface;
        private PluginConfiguration config;
        private WindowManager ui;
        private CommandManager commandManager;

        private Addon talkAddonInterface;

        private SpeechSynthesizer speechSynthesizer;
        private WsServer wsServer;
        private SharedState sharedState;

        private PluginServiceCollection serviceCollection;

        public string Name => "TextToTalk";

        public void Initialize(DalamudPluginInterface pi)
        {
            this.pluginInterface = pi;

            this.config = (PluginConfiguration)this.pluginInterface.GetPluginConfig() ?? new PluginConfiguration();
            this.config.Initialize(this.pluginInterface);

            this.wsServer = new WsServer(config.WebsocketPort);
            this.speechSynthesizer = new SpeechSynthesizer();
            this.sharedState = new SharedState();

            this.serviceCollection = new PluginServiceCollection();
            this.serviceCollection.AddService(this.config);
            this.serviceCollection.AddService(this.wsServer);
            this.serviceCollection.AddService(this.sharedState);
            this.serviceCollection.AddService(this.speechSynthesizer);
            this.serviceCollection.AddService(this.pluginInterface, shouldDispose: false);

            this.ui = new WindowManager(this.serviceCollection);
            this.serviceCollection.AddService(this.ui, shouldDispose: false);

            this.ui.AddWindow<UnlockerResultWindow>(initiallyVisible: false);
            this.ui.AddWindow<VoiceUnlockerWindow>(initiallyVisible: false);
            this.ui.AddWindow<ConfigurationWindow>(initiallyVisible: false);

            this.pluginInterface.UiBuilder.OnBuildUi += this.ui.Draw;
            this.pluginInterface.UiBuilder.OnOpenConfigUi += OpenConfigUi;

            this.pluginInterface.Framework.Gui.Chat.OnChatMessage += OnChatMessage;

            this.pluginInterface.Framework.OnUpdateEvent += PollTalkAddon;
            this.pluginInterface.Framework.OnUpdateEvent += CheckKeybindPressed;

            this.commandManager = new CommandManager(pi, this.serviceCollection);
            this.commandManager.AddCommandModule<MainCommandModule>();

            //AWS - Init
            InitAWS(config);
        }
        public static void InitAWS(PluginConfiguration Configuration) 
        {
            PluginLog.Log("AWS: Init Basic Credentials");
            var AccessKeyID = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
            var SecretAccessKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
            if (AccessKeyID != "" || SecretAccessKey == "")
            { 
            AWSCredentials = new Amazon.Runtime.BasicAWSCredentials(AccessKeyID, SecretAccessKey);

            PluginLog.Log("AWS: Init Polly Client");
            PollyClient = new Amazon.Polly.AmazonPollyClient(AWSCredentials, Amazon.RegionEndpoint.EUWest2);

            PluginLog.Log("AWS: Get list of English US Voices"); 
            var VReq = new Amazon.Polly.Model.DescribeVoicesRequest();
            VReq.Engine = Configuration.Engine;
            VReq.LanguageCode = "en-US";
            VReq.IncludeAdditionalLanguageCodes = true;

            PluginLog.Log("AWS: Save Objects to Config");
            Voices = PollyClient.DescribeVoices(VReq);
            }
        }

        private bool keysDown;
        private void CheckKeybindPressed(Framework framework)
        {
            if (!this.config.UseKeybind) return;

            if (this.pluginInterface.ClientState.KeyState[(byte)this.config.ModifierKey] &&
                this.pluginInterface.ClientState.KeyState[(byte)this.config.MajorKey])
            {
                if (this.keysDown) return;

                this.keysDown = true;

                var commandModule = this.commandManager.GetCommandModule<MainCommandModule>();
                commandModule.ToggleTts();

                return;
            }

            this.keysDown = false;
        }

        private unsafe void PollTalkAddon(Framework framework)
        {
            if (!this.config.Enabled) return;
            if (!this.config.ReadFromQuestTalkAddon) return;

            if (this.talkAddonInterface == null || this.talkAddonInterface.Address == IntPtr.Zero)
            {
                this.talkAddonInterface = this.pluginInterface.Framework.Gui.GetAddonByName("Talk", 1);
                return;
            }

            var talkAddon = (AddonTalk*)this.talkAddonInterface.Address.ToPointer();
            if (talkAddon == null) return;

            var talkAddonText = TalkUtils.ReadTalkAddon(this.pluginInterface.Data, talkAddon);
            var text = talkAddonText.Text;

            if (talkAddonText.Text == "" || IsDuplicateQuestText(talkAddonText.Text)) return;
            SetLastQuestText(text);

#if DEBUG
            PluginLog.Log($"NPC text found: \"{text}\"");
#endif

            if (talkAddonText.Speaker != "" && ShouldSaySender())
            {
                if (!this.config.DisallowMultipleSay || !IsSameSpeaker(talkAddonText.Speaker))
                {
                    text = $"{talkAddonText.Speaker} says {text}";
                    SetLastSpeaker(talkAddonText.Speaker);
                }
            }

            SayAsync(text);
        }

        private void OnChatMessage(XivChatType type, uint id, ref SeString sender, ref SeString message, ref bool handled)
        {
            if (!this.config.Enabled) return;

            var textValue = message.TextValue;
            if (IsDuplicateQuestText(textValue)) return;

            if (sender != null && sender.TextValue != string.Empty)
            {
                if (ShouldSaySender(type))
                {
                    if (!this.config.DisallowMultipleSay || !IsSameSpeaker(sender.TextValue))
                    {
                        if ((int)type == (int)AdditionalChatTypes.Enum.NPCDialogue)
                        {
                            SetLastQuestText(textValue);
                        }
                        textValue = $"{sender.TextValue} says {textValue}";
                        SetLastSpeaker(sender.TextValue);
                    }
                }
            }

#if DEBUG
            PluginLog.Log("Chat message from type {0}: {1}", type, textValue);
#endif

            if (this.config.Bad.Where(t => t.Text != "").Any(t => t.Match(textValue))) return;

            var chatTypes = this.config.GetCurrentEnabledChatTypesPreset();

            //Nothing should be active on mine, what did you break Karashiiro? XD
            foreach (var v in chatTypes.EnabledChatTypes)
            {
                PluginLog.Log(v.ToString());
            }
            

            var typeAccepted = chatTypes.EnabledChatTypes.Contains((int)type);
            var goodMatch = this.config.Good
                .Where(t => t.Text != "")
                .Any(t => t.Match(textValue));
            if (!(chatTypes.EnableAllChatTypes || typeAccepted) || this.config.Good.Count > 0 && !goodMatch) return;
            //Commenting out chat types for now
            //SayAsync(textValue);
        }

        private async Task SayAsync(string textValue)
        {
            var cleanText = TalkUtils.StripSSMLTokens(textValue);

            if (this.config.Synthesizer == "Websocket Server")
            {
                this.wsServer.Broadcast(cleanText);
#if DEBUG
                PluginLog.Log("Sent message {0} on WebSocket server.", textValue);
#endif
            }
            else if (this.config.Synthesizer == "Microsoft Voices")
            {
                this.speechSynthesizer.Rate = this.config.Rate;
                this.speechSynthesizer.Volume = this.config.Volume;

                if (this.speechSynthesizer.Voice.Name != this.config.VoiceName)
                {
                    this.speechSynthesizer.SelectVoice(this.config.VoiceName);
                }

                this.speechSynthesizer.SpeakAsync(cleanText);
            }
            else if (this.config.Synthesizer == "AWS Polly")
            {
                await Task.Run(() =>
                {
                    Amazon.Polly.AmazonPollyClient cl = PollyClient;
                    PluginLog.Log("Polly Client Init");
                    Amazon.Polly.Model.SynthesizeSpeechRequest req = new Amazon.Polly.Model.SynthesizeSpeechRequest();
                    PluginLog.Log("Polly Engine: " + config.Engine);
                    req.Engine = config.Engine;
                    req.Text = cleanText;
                    foreach (var V in Voices.Voices)
                    {
                        if (V.Name == config.PollyVoice)
                        {
                            req.VoiceId = V.Id;
                        }
                    }
                    req.OutputFormat = Amazon.Polly.OutputFormat.Mp3;
                    req.TextType = Amazon.Polly.TextType.Text;
                    
                    Amazon.Polly.Model.SynthesizeSpeechResponse resp = cl.SynthesizeSpeech(req);
                    MemoryStream local_stream = new MemoryStream();
                    resp.AudioStream.CopyTo(local_stream);
                    local_stream.Position = 0;
                    PluginLog.Log("Got mp3 stream, length: " + local_stream.Length.ToString());
                
                    NAudio.Wave.Mp3FileReader reader = new NAudio.Wave.Mp3FileReader(local_stream);
                    NAudio.Wave.WaveStream wave_stream = NAudio.Wave.WaveFormatConversionStream.CreatePcmStream(reader);
                    NAudio.Wave.BlockAlignReductionStream ba_stream = new NAudio.Wave.BlockAlignReductionStream(wave_stream);
                    
                    //Moved wout to global static so I can stop the previous audiostream
                    //Seriously not gonna create a queue system.
                    PluginLog.Log("Playing stream...");
                    wout.Stop(); 
                    wout.Init(ba_stream);
                    wout.Play();
                    while (wout.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                    {
                        Thread.Sleep(100);
                    }
                });
            }
        }

        private void OpenConfigUi(object sender, EventArgs args)
        {
            this.ui.ShowWindow<ConfigurationWindow>();
        }

        private bool IsDuplicateQuestText(string text)
        {
            return this.sharedState.LastQuestText == text;
        }

        private void SetLastQuestText(string text)
        {
            this.sharedState.LastQuestText = text;
        }

        private bool IsSameSpeaker(string speaker)
        {
            return this.sharedState.LastSpeaker == speaker;
        }

        private void SetLastSpeaker(string speaker)
        {
            this.sharedState.LastSpeaker = speaker;
        }

        private bool ShouldSaySender()
        {
            return this.config.NameNpcWithSay;
        }

        private bool ShouldSaySender(XivChatType type)
        {
            return this.config.NameNpcWithSay || (int)type != (int)AdditionalChatTypes.Enum.NPCDialogue;
        }

        #region IDisposable Support
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            this.commandManager.Dispose();

            this.pluginInterface.Framework.OnUpdateEvent -= PollTalkAddon;
            this.pluginInterface.Framework.OnUpdateEvent -= CheckKeybindPressed;

            this.pluginInterface.Framework.Gui.Chat.OnChatMessage -= OnChatMessage;

            this.wsServer.Stop();

            this.pluginInterface.SavePluginConfig(this.config);

            this.pluginInterface.UiBuilder.OnOpenConfigUi -= OpenConfigUi;
            this.pluginInterface.UiBuilder.OnBuildUi -= this.ui.Draw;

            this.ui.Dispose();

            this.serviceCollection.Dispose();

            this.pluginInterface.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
