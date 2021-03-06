using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HotChocolate;
using HotChocolate.Execution.Configuration;
using HotChocolate.Execution.Errors;

namespace Microsoft.Extensions.DependencyInjection
{
    public static partial class RequestExecutorBuilderExtensions
    {
        public static IRequestExecutorBuilder AddErrorFilter(
            this IRequestExecutorBuilder builder,
            Func<IError, IError> errorFilter)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (errorFilter == null)
            {
                throw new ArgumentNullException(nameof(errorFilter));
            }

            return builder.ConfigureSchemaServices(
                s => s.AddSingleton<IErrorFilter>(
                    new FuncErrorFilterWrapper(errorFilter)));
        }

        public static IRequestExecutorBuilder AddErrorFilter(
            this IRequestExecutorBuilder builder,
            Func<IServiceProvider, IErrorFilter> factory)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            return builder.ConfigureSchemaServices(
                s => s.AddSingleton<IErrorFilter>(
                    sp => factory(sp.GetRequiredService<IApplicationServiceProvider>())));
        }

        public static IRequestExecutorBuilder AddErrorFilter<T>(
            this IRequestExecutorBuilder builder)
            where T : class, IErrorFilter
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            builder.Services.TryAddTransient<T>();
            return builder.ConfigureSchemaServices(
                s => s.AddSingleton<IErrorFilter>(
                    sp => sp.GetRequiredService<IApplicationServiceProvider>()
                        .GetRequiredService<T>()));
        }

        public static IServiceCollection AddErrorFilter(
            this IServiceCollection services,
            Func<IError, IError> errorFilter)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (errorFilter == null)
            {
                throw new ArgumentNullException(nameof(errorFilter));
            }

            return services.AddSingleton<IErrorFilter>(
                new FuncErrorFilterWrapper(errorFilter));
        }

        public static IServiceCollection AddErrorFilter(
            this IServiceCollection services,
            Func<IServiceProvider, IErrorFilter> factory)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            return services.AddSingleton(factory);
        }

        public static IServiceCollection AddErrorFilter<T>(
            this IServiceCollection services)
            where T : class, IErrorFilter
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            return services.AddSingleton<IErrorFilter, T>();
        }
    }
}