﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;
using DNetPlus_InteractiveButtons;
using System.Linq;
using Newtonsoft.Json;
using Discord.Net.Converters;

namespace TestBot
{
    public class Program
    {
        public static DiscordShardedClient Client;
        public static CommandService Commands;
        public static CommandHandler Handler;
        public static IServiceProvider _services;
        public static void Main(string[] args)
        {
            Start().GetAwaiter().GetResult();
        }
        public static async Task Start()
        {
            Client = new DiscordShardedClient(new DiscordSocketConfig
            {
                OwnerIds = new ulong[] { 190590364871032834 },
                AlwaysDownloadUsers = false,
                TotalShards = 1,
                GatewayIntents = GatewayIntents.DirectMessages | GatewayIntents.DirectMessageReactions | GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMembers,
                //MaxWaitBetweenGuildAvailablesBeforeReady = 5000,
                LogLevel = Discord.LogSeverity.Debug
            });
            string File = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "/DiscordBots/Boaty/Config.json";
            Client.Log += Client_Log;
            Client.UserJoinRequestDeleted += Client_UserJoinRequestDeleted;
            await Client.LoginAsync(Discord.TokenType.Bot, JObject.Parse(System.IO.File.ReadAllText(File))["Discord"].ToString());
            await Client.StartAsync();

            Client.InteractionReceived += Client_InteractionReceived;
            Commands = new CommandService(new CommandServiceConfig { DefaultRunMode = RunMode.Async });
            _services = BuildServiceProvider();
            Handler = new CommandHandler(Client, Commands, _services);
            
            await Handler.InstallCommandsAsync();
            await Task.Delay(-1);
        }

        public static IServiceProvider BuildServiceProvider() => new ServiceCollection()
        .AddSingleton(Client)
        .AddSingleton(Commands)
        .AddSingleton(new InteractiveButtonsService(Client))
        
        .AddSingleton<CommandHandler>()
        .BuildServiceProvider();

        private static async Task Client_UserJoinRequestDeleted(SocketUser arg1, SocketGuild arg2)
        {
            // Fix why socketuser can be null sometimes.

            Console.WriteLine("GOT LEAVE");
            //Console.WriteLine($"User: {arg1.Username} Guild: {arg2.Name}");
        }

        private static async Task Client_InteractionReceived(Interaction arg)
        {
            switch (arg.Type)
            {
                case InteractionType.ApplicationCommand:
                    
                    ShardedCommandContext context = new ShardedCommandContext(Client, arg);
                    Commands.ExecuteAsync(context: context, argPos: 0, services: _services);
                    break;
                case InteractionType.MessageComponent:
                    if (arg.User.Id == 190590364871032834)
                    {
                        Console.Write("Button Trigger");
                        if (arg.Data.ComponentType == ComponentType.Dropdown)
                        {
                            
                        }
                        arg.Channel.SendInteractionMessageAsync(arg.Data, "Test Button", type: Discord.API.Rest.InteractionMessageType.ChannelMessageWithSource, ghostMessage: true, components: new InteractionRow[]
                        {
                            new InteractionRow
                {
                    Buttons = new InteractionButton[]
                    {
                        new InteractionButton(ComponentButtonType.Primary, "Test", "test")
                    }
                }
                        });
                        try
                        {
                            //await arg.Channel.SendInteractionMessageAsync(arg.Data, $"Test button clicked!");
                        }
                        catch(Exception ex)
                        {
                            Console.WriteLine(ex);
                        }
                    }
                    break;
            }
            
        }

        private static async Task Client_Log(Discord.LogMessage arg)
        {
            Console.WriteLine($"[{arg.Source}] {arg.Message}");
        }
    }
}
