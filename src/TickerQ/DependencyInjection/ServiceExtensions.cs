﻿using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using TickerQ.Src.Provider;
using TickerQ.Utilities;
using TickerQ.Utilities.Enums;
using TickerQ.Utilities.Interfaces;
using TickerQ.Utilities.Interfaces.Managers;
using TickerQ.Utilities.Managers;
using TickerQ.Utilities.Models.Ticker;
using TickerQ.Utilities.Temps;

namespace TickerQ.DependencyInjection
{
    public static class ServiceExtensions
    {
        /// <summary>
        /// Adds Ticker to the service collection.
        /// </summary>
        /// <param name="services"></param>
        /// <param name="optionsBuilder"></param>
        /// <returns></returns>
        public static IServiceCollection AddTickerQ(this IServiceCollection services, Action<TickerOptionsBuilder> optionsBuilder = null)
        {
            services.AddScoped<ICronTickerManager<CronTicker>, TickerManager<TimeTicker, CronTicker>>();
            services.AddScoped<ITimeTickerManager<TimeTicker>, TickerManager<TimeTicker, CronTicker>>();
            services.AddScoped<IInternalTickerManager, TickerManager<TimeTicker, CronTicker>>();

            services.AddSingleton<ITickerPersistenceProvider<TimeTicker, CronTicker>, TickerInMemoryPersistenceProvider<TimeTicker, CronTicker>>();

            var optionInstance = new TickerOptionsBuilder();

            optionsBuilder?.Invoke(optionInstance);

            if (optionInstance.ExternalProviderConfigServiceAction != null)
                optionInstance.UseExternalProvider(services);

            if (optionInstance.TickerExceptionHandlerType != null)
                services.AddScoped(typeof(ITickerExceptionHandler), optionInstance.TickerExceptionHandlerType);

            if (optionInstance.MaxConcurrency <= 0)
                optionInstance.SetMaxConcurrency(0);

            if (string.IsNullOrEmpty(optionInstance.InstanceIdentifier))
                optionInstance.SetInstanceIdentifier(Environment.MachineName);

            if (optionInstance.DashboardServiceAction != null)
                optionInstance.DashboardServiceAction(services);
            else
                services.AddSingleton<ITickerQNotificationHubSender, TempTickerQNotificationHubSender>();

            services.AddSingleton<ITickerClock, TickerSystemClock>();

            services.AddSingleton<TickerOptionsBuilder>(_ => optionInstance)
                .AddSingleton<ITickerHost, TickerHost>();

            return services;
        }

        /// <summary>
        /// Use Ticker in the application.
        /// </summary>
        /// <param name="app"></param>
        /// <param name="qStartMode"></param>
        /// <returns></returns>
        public static IApplicationBuilder UseTickerQ(this IApplicationBuilder app, TickerQStartMode qStartMode = TickerQStartMode.Immediate)
        {
            var tickerOptBuilder = app.ApplicationServices.GetService<TickerOptionsBuilder>();
            var configuration = app.ApplicationServices.GetService<IConfiguration>();

            MapCronFromConfig(configuration);

            if (tickerOptBuilder is { DashboardApplicationAction: { } })
                tickerOptBuilder.DashboardApplicationAction.Invoke(app, tickerOptBuilder.DashboardLunchUrl);

            tickerOptBuilder.NotifyNextOccurenceFunc = nextOccurrence =>
            {
                var notificationHubSender = app.ApplicationServices.GetService<ITickerQNotificationHubSender>();

                notificationHubSender.UpdateNextOccurrence(nextOccurrence);
            };

            tickerOptBuilder.NotifyHostStatusFunc = active =>
            {
                var notificationHubSender = app.ApplicationServices.GetService<ITickerQNotificationHubSender>();

                notificationHubSender.UpdateHostStatus(active);
            };

            tickerOptBuilder.HostExceptionMessageFunc = message =>
            {
                tickerOptBuilder.LastHostExceptionMessage = message;
                var notificationHubSender = app.ApplicationServices.GetService<ITickerQNotificationHubSender>();

                notificationHubSender.UpdateHostException(message);
            };
            
            if(tickerOptBuilder.ExternalProviderConfigApplicationAction != null)
                tickerOptBuilder.ExternalProviderConfigApplicationAction(app);
            else
                SeedDefinedCronTickers(app);

            if (qStartMode == TickerQStartMode.Manual) return app;

            var tickerHost = app.ApplicationServices.GetRequiredService<ITickerHost>();

            tickerHost.Start();

            return app;
        }

        private static void MapCronFromConfig(IConfiguration configuration)
        {
            var tickerFunctions =
                new Dictionary<string, (string cronExpression, TickerTaskPriority Priority, TickerFunctionDelegate
                    Delegate)>(TickerFunctionProvider.TickerFunctions ?? new Dictionary<string, (string, TickerTaskPriority, TickerFunctionDelegate)>());

            foreach (var (key, value) in tickerFunctions)
            {
                if (!value.cronExpression.StartsWith("%")) continue;
                
                var mappedCronExpression = configuration[value.cronExpression.Trim('%')];
                tickerFunctions[key] = (mappedCronExpression, value.Priority, value.Delegate);
            }
            TickerFunctionProvider.MapCronExpressionsFromIConfigurations(tickerFunctions);
        }

        private static void SeedDefinedCronTickers(this IApplicationBuilder app)
        {
            using var scope = app.ApplicationServices.CreateScope();

            var internalTickerManager = scope.ServiceProvider.GetRequiredService<IInternalTickerManager>();
                
            var functionsToSeed = TickerFunctionProvider.TickerFunctions
                .Where(x => !string.IsNullOrEmpty(x.Value.cronExpression))
                .Select(x => (x.Key, x.Value.cronExpression)).ToArray();
                
            internalTickerManager.SyncWithDbMemoryCronTickers(functionsToSeed).GetAwaiter().GetResult();
        }
    }
}
