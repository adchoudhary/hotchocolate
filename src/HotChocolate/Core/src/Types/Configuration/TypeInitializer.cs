using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HotChocolate.Configuration.Validation;
using HotChocolate.Internal;
using HotChocolate.Properties;
using HotChocolate.Resolvers;
using HotChocolate.Resolvers.Expressions;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;
using HotChocolate.Types.Descriptors.Definitions;
using HotChocolate.Utilities;
using static HotChocolate.Properties.TypeResources;

#nullable enable

namespace HotChocolate.Configuration
{
    internal class TypeInitializer
    {
        private readonly Dictionary<RegisteredType, TypeCompletionContext> _completionContext =
            new Dictionary<RegisteredType, TypeCompletionContext>();
        private readonly Dictionary<FieldReference, RegisteredResolver> _resolvers =
            new Dictionary<FieldReference, RegisteredResolver>();
        private readonly List<FieldMiddleware> _globalComps = new List<FieldMiddleware>();
        private readonly List<ISchemaError> _errors = new List<ISchemaError>();
        private readonly IDescriptorContext _context;
        private readonly ITypeInspector _typeInspector;
        private readonly IReadOnlyList<ITypeReference> _initialTypes;
        private readonly IReadOnlyList<Type> _externalResolverTypes;
        private readonly ITypeInterceptor _interceptor;
        private readonly IsOfTypeFallback _isOfType;
        private readonly Func<TypeSystemObjectBase, bool> _isQueryType;
        private readonly TypeRegistry _typeRegistry;
        private readonly TypeLookup _typeLookup;
        private readonly TypeReferenceResolver _typeReferenceResolver;

        public TypeInitializer(
            IDescriptorContext descriptorContext,
            TypeRegistry typeRegistry,
            IReadOnlyList<ITypeReference> initialTypes,
            IReadOnlyList<Type> externalResolverTypes,
            ITypeInterceptor interceptor,
            IsOfTypeFallback isOfType,
            Func<TypeSystemObjectBase, bool> isQueryType)
        {
            _context = descriptorContext ??
                throw new ArgumentNullException(nameof(descriptorContext));
            _typeRegistry = typeRegistry ??
                throw new ArgumentNullException(nameof(typeRegistry));
            _initialTypes = initialTypes ??
                throw new ArgumentNullException(nameof(initialTypes));
            _externalResolverTypes = externalResolverTypes ??
                throw new ArgumentNullException(nameof(externalResolverTypes));
            _interceptor = interceptor ??
                throw new ArgumentNullException(nameof(interceptor));
            _isOfType = isOfType ??
                throw new ArgumentNullException(nameof(isOfType));
            _isQueryType = isQueryType ??
                throw new ArgumentNullException(nameof(isQueryType));

            _typeInspector = descriptorContext.TypeInspector;
            _typeLookup = new TypeLookup(_typeInspector, _typeRegistry);
            _typeReferenceResolver = new TypeReferenceResolver(
                _typeInspector, _typeRegistry, _typeLookup);
        }

        public IList<FieldMiddleware> GlobalComponents => _globalComps;

        public IDictionary<FieldReference, RegisteredResolver> Resolvers => _resolvers;

        public void Initialize(
            Func<ISchema> schemaResolver,
            IReadOnlySchemaOptions options)
        {
            // first we are going to find and initialize all types that belong to our schema.
            var typeRegistrar = new TypeDiscoverer(
                _typeRegistry, new HashSet<ITypeReference>(_initialTypes),
                _context, _interceptor);

            if (typeRegistrar.DiscoverTypes() is { Count: > 0 } errors)
            {
                throw new SchemaException(errors);
            }

            // next lets tell the type interceptors what types we have initialized.
            if (_interceptor.TriggerAggregations)
            {
                _interceptor.OnTypesInitialized(
                    _typeRegistry.Types.Select(t => t.DiscoveryContext).ToList());
            }

            // before we can start completing type names we need to register the field resolvers.
            RegisterResolvers();

            // now that we have the resolvers sorted and know what types our schema will roughly 
            // consist of we are going to have a look if we can infer interface usage 
            // from .NET classes that implement .NET interfaces.
            RegisterImplicitInterfaceDependencies();

            CompleteNames(_discoveredTypes, schemaResolver);
            MergeTypeExtensions(_discoveredTypes);
            RegisterExternalResolvers(_discoveredTypes);
            CompileResolvers();
            CompleteTypes(_discoveredTypes);


            _errors.AddRange(_discoveredTypes.Errors);

            if (_errors.Count == 0)
            {
                _errors.AddRange(SchemaValidator.Validate(
                    _discoveredTypes.Types.Select(t => t.Type),
                    options));
            }

            if (_errors.Count > 0)
            {
                throw new SchemaException(_errors);
            }

            return _discoveredTypes;
        }

        private void RegisterResolvers()
        {
            foreach (TypeDiscoveryContext context in
                _typeRegistry.Types.Select(t => t.DiscoveryContext))
            {
                foreach (FieldReference reference in context.Resolvers.Keys)
                {
                    if (!_resolvers.ContainsKey(reference))
                    {
                        _resolvers[reference] = context.Resolvers[reference];
                    }
                }
            }
        }

        private void RegisterImplicitInterfaceDependencies()
        {
            var withRuntimeType = _typeRegistry.Types
                .Where(t => t.RuntimeType != typeof(object))
                .Distinct()
                .ToList();

            var interfaceTypes = withRuntimeType
                .Where(t => t.Type is InterfaceType)
                .Distinct()
                .ToList();

            var objectTypes = withRuntimeType
                .Where(t => t.Type is ObjectType)
                .Distinct()
                .ToList();

            var dependencies = new List<TypeDependency>();

            foreach (RegisteredType objectType in objectTypes)
            {
                foreach (RegisteredType interfaceType in interfaceTypes)
                {
                    if (interfaceType.RuntimeType.IsAssignableFrom(objectType.RuntimeType))
                    {
                        dependencies.Add(
                            new TypeDependency(
                                _typeInspector.GetTypeRef(
                                    interfaceType.RuntimeType,
                                    TypeContext.Output),
                                TypeDependencyKind.Completed));
                    }
                }

                if (dependencies.Count > 0)
                {
                    dependencies.AddRange(objectType.Dependencies);
                    _typeRegistry.Register(objectType.WithDependencies(dependencies));
                    dependencies = new List<TypeDependency>();
                }
            }

            _typeRegistry.RebuildIndexes();
        }

        private void CompleteNames(Func<ISchema> schemaResolver)
        {
            var success = CompleteTypes(
                TypeDependencyKind.Named,
                registeredType =>
                {
                    TypeDiscoveryContext initializationContext =
                        _initContexts.First(t =>
                            t.Type == registeredType.Type);

                    var completionContext = new TypeCompletionContext(
                        initializationContext,
                        _typeReferenceResolver,
                        GlobalComponents,
                        Resolvers,
                        _isOfType,
                        schemaResolver);

                    _completionContext[registeredType] = completionContext;

                    registeredType.Type.CompleteName(completionContext);

                    if (registeredType.Type is INamedType
                        || registeredType.Type is DirectiveType)
                    {
                        if (_named.ContainsKey(registeredType.Type.Name))
                        {
                            _errors.Add(SchemaErrorBuilder.New()
                                .SetMessage(string.Format(
                                    CultureInfo.InvariantCulture,
                                    TypeResources.TypeInitializer_CompleteName_Duplicate,
                                    registeredType.Type.Name))
                                .SetTypeSystemObject(registeredType.Type)
                                .Build());
                            return false;
                        }
                        _named[registeredType.Type.Name] =
                            registeredType.References[0];
                    }

                    return true;
                });

            if (success)
            {
                UpdateDependencyLookup();

                if (_interceptor.TriggerAggregations)
                {
                    _interceptor.OnTypesCompletedName(
                        _completionContext.Values.Where(t => t is { }).ToList());
                }
            }

            EnsureNoErrors();
        }

        private void UpdateDependencyLookup()
        {
            if (_discoveredTypes is { })
            {
                foreach (RegisteredType registeredType in _discoveredTypes.Types)
                {
                    TryNormalizeDependencies(
                        registeredType.Dependencies.Select(t => t.TypeReference),
                        out _);
                }
            }
        }

        private void MergeTypeExtensions()
        {
            var extensions = _typeRegistry.Types
                .Where(t => t.IsExtension)
                .ToList();

            if (extensions.Count > 0)
            {
                var types = _typeRegistry.Types
                    .Where(t => t.IsNamedType)
                    .ToList();

                foreach (IGrouping<NameString, RegisteredType> group in
                    extensions.GroupBy(t => t.Type.Name))
                {
                    RegisteredType? type =
                        types.FirstOrDefault(t => t.Type.Name.Equals(group.Key));

                    if (type != null && type.Type is INamedType targetType)
                    {
                        MergeTypeExtension(group, type, targetType);
                    }
                }

                _typeRegistry.RebuildIndexes();
            }
        }

        private void MergeTypeExtension(
            IEnumerable<RegisteredType> extensions,
            RegisteredType type,
            INamedType targetType)
        {
            foreach (RegisteredType extension in extensions)
            {
                if (extension.Type is INamedTypeExtensionMerger m)
                {
                    if (m.Kind != targetType.Kind)
                    {
                        throw new SchemaException(SchemaErrorBuilder.New()
                            .SetMessage(string.Format(
                                CultureInfo.InvariantCulture,
                                TypeResources.TypeInitializer_Merge_KindDoesNotMatch,
                                targetType.Name))
                            .SetTypeSystemObject((ITypeSystemObject)targetType)
                            .Build());
                    }

                    TypeDiscoveryContext initContext = extension.DiscoveryContext;
                    foreach (FieldReference reference in initContext.Resolvers.Keys)
                    {
                        _resolvers[reference]
                            = initContext.Resolvers[reference].WithSourceType(type.RuntimeType);
                    }

                    // merge
                    TypeCompletionContext context = _completionContext[extension];
                    context.Status = TypeStatus.Named;
                    m.Merge(context, targetType);

                    // update dependencies
                    context = _completionContext[type];
                    type = type.AddDependencies(extension.Dependencies);
                    _typeRegistry.Register(type);
                    _completionContext[type] = context;
                    CopyAlternateNames(_completionContext[extension], context);
                }
            }
        }

        private static void CopyAlternateNames(
            TypeCompletionContext source,
            TypeCompletionContext destination)
        {
            foreach (NameString name in source.AlternateTypeNames)
            {
                destination.AlternateTypeNames.Add(name);
            }
        }

        private void RegisterExternalResolvers()
        {
            if (_externalResolverTypes.Count == 0)
            {
                return;
            }

            Dictionary<NameString, ObjectType> types =
                discoveredTypes.Types.Select(t => t.Type)
                    .OfType<ObjectType>()
                    .ToDictionary(t => t.Name);

            foreach (Type type in _externalResolverTypes)
            {
                GraphQLResolverOfAttribute? attribute =
                    type.GetCustomAttribute<GraphQLResolverOfAttribute>();

                if (attribute?.TypeNames != null)
                {
                    foreach (string typeName in attribute.TypeNames)
                    {
                        if (types.TryGetValue(typeName, out ObjectType? objectType))
                        {
                            AddResolvers(_context, objectType, type);
                        }
                    }
                }

                if (attribute?.Types != null)
                {
                    foreach (Type sourceType in attribute.Types
                        .Where(t => !t.IsNonGenericSchemaType()))
                    {

                        ObjectType? objectType = types.Values
                            .FirstOrDefault(t => t.GetType() == sourceType
                                || t.RuntimeType == sourceType);
                        if (objectType is not null)
                        {
                            AddResolvers(_context, objectType, type);
                        }
                    }
                }
            }
        }

        private void AddResolvers(
            IDescriptorContext context,
            ObjectType objectType,
            Type resolverType)
        {
            foreach (MemberInfo member in
                context.TypeInspector.GetMembers(resolverType))
            {
                if (IsResolverRelevant(objectType.RuntimeType, member))
                {
                    NameString fieldName = context.Naming.GetMemberName(
                        member, MemberKind.ObjectField);
                    var fieldMember = new FieldMember(
                        objectType.Name, fieldName, member);
                    var resolver = new RegisteredResolver(
                        resolverType, objectType.RuntimeType, fieldMember);
                    _resolvers[fieldMember.ToFieldReference()] = resolver;
                }
            }
        }

        private static bool IsResolverRelevant(
            Type sourceType,
            MemberInfo resolver)
        {
            if (resolver is PropertyInfo)
            {
                return true;
            }

            if (resolver is MethodInfo m)
            {
                ParameterInfo? parent = m.GetParameters()
                    .FirstOrDefault(t => t.IsDefined(typeof(ParentAttribute)));
                return parent is null
                    || parent.ParameterType.IsAssignableFrom(sourceType);
            }

            return false;
        }

        private void CompileResolvers()
        {
            foreach (KeyValuePair<FieldReference, RegisteredResolver> item in _resolvers.ToArray())
            {
                RegisteredResolver registered = item.Value;
                if (registered.Field is FieldMember member)
                {
                    ResolverDescriptor descriptor =
                        registered.IsSourceResolver
                            ? new ResolverDescriptor(
                                registered.SourceType,
                                member)
                            : new ResolverDescriptor(
                                registered.ResolverType,
                                registered.SourceType,
                                member);
                    _resolvers[item.Key] = registered.WithField(
                        ResolverCompiler.Resolve.Compile(descriptor));
                }
            }
        }

        

        

        private void CompleteTypes()
        {
            ProcessTypes(
                TypeDependencyKind.Completed,
                registeredType =>
                {
                    if (!registeredType.IsExtension)
                    {
                        TypeCompletionContext context = _completionContext[registeredType];
                        context.Status = TypeStatus.Named;
                        context.IsQueryType = _isQueryType.Invoke(registeredType.Type);
                        registeredType.Type.CompleteType(context);
                    }
                    return true;
                });

            EnsureNoErrors();

            if (_interceptor.TriggerAggregations)
            {
                _interceptor.OnTypesCompleted(
                    _completionContext.Values.Where(t => t is { }).ToList());
            }
        }

        private bool ProcessTypes(
            TypeDependencyKind kind,
            Func<RegisteredType, bool> action)
        {
            var processed = new HashSet<ITypeReference>();
            var batch = new List<RegisteredType>(GetInitialBatch(kind));
            var failed = false;

            while (!failed
                && processed.Count < _typeRegistry.Count
                && batch.Count > 0)
            {
                foreach (RegisteredType registeredType in batch)
                {
                    if (!action(registeredType))
                    {
                        failed = true;
                        break;
                    }

                    foreach (ITypeReference reference in registeredType.References)
                    {
                        processed.Add(reference);
                    }
                }

                if (!failed)
                {
                    batch.Clear();
                    batch.AddRange(GetNextBatch(discoveredTypes, processed, kind));
                }
            }

            if (!failed && processed.Count < discoveredTypes.TypeReferenceCount)
            {
                foreach (RegisteredType type in discoveredTypes.Types
                    .Where(t => !processed.Contains(t.References[0])))
                {
                    string name = type.Type.Name.HasValue
                        ? type.Type.Name.Value
                        : type.References[0].ToString()!;

                    ITypeReference[] references =
                        type.Dependencies.Where(t => t.Kind == kind)
                            .Select(t => t.TypeReference).ToArray();

                    IReadOnlyList<ITypeReference> needed =
                        TryNormalizeDependencies(references,
                            out IReadOnlyList<ITypeReference>? normalized)
                            ? normalized.Except(processed).ToArray()
                            : references;

                    _errors.Add(SchemaErrorBuilder.New()
                        .SetMessage(
                            TypeInitializer_CannotResolveDependency,
                            name,
                            string.Join(", ", needed))
                        .SetTypeSystemObject(type.Type)
                        .Build());
                }

                return false;
            }

            return _errors.Count == 0;
        }

        private IEnumerable<RegisteredType> GetInitialBatch(
            TypeDependencyKind kind)
        {
            return _typeRegistry.Types.Where(t => t.Dependencies.All(d => d.Kind != kind));
        }

        private IEnumerable<RegisteredType> GetNextBatch(
            ISet<ITypeReference> processed,
            TypeDependencyKind kind)
        {
            foreach (RegisteredType type in _typeRegistry.Types)
            {
                if (!processed.Contains(type.References[0]))
                {
                    IEnumerable<ITypeReference> references =
                        type.Dependencies.Where(t => t.Kind == kind)
                            .Select(t => t.TypeReference);

                    if (TryNormalizeDependencies(references,
                        out IReadOnlyList<ITypeReference>? normalized)
                        && processed.IsSupersetOf(normalized))
                    {
                        yield return type;
                    }
                }
            }
        }

        private bool TryNormalizeDependencies(
            IEnumerable<ITypeReference> dependencies,
            [NotNullWhen(true)] out IReadOnlyList<ITypeReference>? normalized)
        {
            var n = new List<ITypeReference>();

            foreach (ITypeReference reference in dependencies)
            {
                if (!_typeLookup.TryNormalizeReference(
                    reference,
                    out ITypeReference? nr))
                {
                    normalized = null;
                    return false;
                }

                if (!n.Contains(nr))
                {
                    n.Add(nr);
                }
            }

            normalized = n;
            return true;
        }

        private void EnsureNoErrors()
        {
            var errors = new List<ISchemaError>(_errors);

            foreach (TypeDiscoveryContext context in _typeRegistry. )
            {
                errors.AddRange(context.Errors);
            }

            if (errors.Count > 0)
            {
                throw new SchemaException(errors);
            }
        }
    }

    internal sealed class ExtendedTypeReferenceEqualityComparer
        : IEqualityComparer<ExtendedTypeReference>
    {
        public bool Equals([AllowNull] ExtendedTypeReference x, [AllowNull] ExtendedTypeReference y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (ReferenceEquals(x, null) || ReferenceEquals(y, null))
            {
                return false;
            }

            if (x.Context != y.Context
                && x.Context != TypeContext.None
                && y.Context != TypeContext.None)
            {
                return false;
            }

            if (!x.Scope.EqualsOrdinal(y.Scope))
            {
                return false;
            }

            return Equals(x.Type, y.Type);
        }

        private static bool Equals([AllowNull] IExtendedType x, [AllowNull] IExtendedType y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (ReferenceEquals(x, null) || ReferenceEquals(y, null))
            {
                return false;
            }

            return ReferenceEquals(x.Type, y.Type) && x.Kind == y.Kind;
        }

        public int GetHashCode([DisallowNull] ExtendedTypeReference obj)
        {
            unchecked
            {
                var hashCode = GetHashCode(obj.Type);

                if (obj.Scope is not null)
                {
                    hashCode ^= obj.GetHashCode() * 397;
                }

                return hashCode;
            }
        }

        private static int GetHashCode([DisallowNull] IExtendedType obj)
        {
            unchecked
            {
                var hashCode = (obj.Type.GetHashCode() * 397)
                   ^ (obj.Kind.GetHashCode() * 397);

                for (var i = 0; i < obj.TypeArguments.Count; i++)
                {
                    hashCode ^= (GetHashCode(obj.TypeArguments[i]) * 397 * i);
                }

                return hashCode;
            }
        }

    }
}
