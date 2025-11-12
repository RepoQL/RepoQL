using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines;
using RepoQL.Indexing.Indexing.Pipelines.Analysis;
using RepoQL.Indexing.Indexing.Pipelines.Classification;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Indexing.Hosting;

[SuppressMessage("Design", "CA1034:Nested types should not be visible")]
public static class IndexingServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddIndexingProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>(IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>? _ = null)
            where TProcessor : class, IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
            => RegisterPipeline<TProcessor>(services, typeof(IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>));

        public IServiceCollection AddIndexingProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>(IAsyncPipeline<IClassifiedArtifact, Records?>? _ = null)
            where TProcessor : class, IAsyncPipeline<IClassifiedArtifact, Records?>
            => RegisterPipeline<TProcessor>(services, typeof(IAsyncPipeline<IClassifiedArtifact, Records?>));

        public IServiceCollection AddIndexingProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>(IAsyncPipeline<IParsedArtifact, Annotation[]>? _ = null)
            where TProcessor : class, IAsyncPipeline<IParsedArtifact, Annotation[]>
            => RegisterPipeline<TProcessor>(services, typeof(IAsyncPipeline<IParsedArtifact, Annotation[]>));

        public IServiceCollection AddIndexingProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>(IAsyncPipeline<IAnnotatedArtifact, Annotation[]>? _ = null)
            where TProcessor : class, IAsyncPipeline<IAnnotatedArtifact, Annotation[]>
            => RegisterPipeline<TProcessor>(services, typeof(IAsyncPipeline<IAnnotatedArtifact, Annotation[]>));

        public IServiceCollection AddIndexingProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>(IAsyncPipeline<IAnnotatedArtifact, string>? _ = null)
            where TProcessor : class, IAsyncPipeline<IAnnotatedArtifact, string>
            => RegisterPipeline<TProcessor>(services, typeof(IAsyncPipeline<IAnnotatedArtifact, string>));

        public IServiceCollection AddIndexingProcessor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>(Type pipelineInterface)
            where TProcessor : class
        {
            ArgumentNullException.ThrowIfNull(pipelineInterface);

            if (!pipelineInterface.IsInterface ||
                !pipelineInterface.IsGenericType ||
                pipelineInterface.GetGenericTypeDefinition() != typeof(IAsyncPipeline<,>))
            {
                throw new ArgumentException(
                    "pipelineInterface must be an interface derived from IAsyncPipeline<TInput, TResult>.",
                    nameof(pipelineInterface));
            }

            if (!pipelineInterface.IsAssignableFrom(typeof(TProcessor)))
            {
                throw new InvalidOperationException(
                    $"{typeof(TProcessor).Name} does not implement {pipelineInterface.Name}.");
            }

            return RegisterPipeline<TProcessor>(services, pipelineInterface);
        }
    }

    private static IServiceCollection RegisterPipeline<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProcessor>(
        IServiceCollection services,
        Type pipelineInterface)
        where TProcessor : class
    {
        services.AddSingleton<TProcessor>();
        services.AddSingleton(pipelineInterface, sp => sp.GetRequiredService<TProcessor>());
        return services;
    }
}
