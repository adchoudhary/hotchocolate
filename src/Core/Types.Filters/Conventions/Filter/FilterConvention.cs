using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HotChocolate.Types.Descriptors;
using HotChocolate.Types.Filters.Properties;

namespace HotChocolate.Types.Filters.Conventions
{
    public delegate bool TryCreateImplicitFilter(
            IDescriptorContext context,
            Type type,
            PropertyInfo property,
            out FilterFieldDefintion definition);

    public delegate NameString CreateFieldName(
            FilterFieldDefintion definition,
            FilterOperationKind kind);

    public delegate NameString GetFilterTypeName(
        IDescriptorContext context,
        Type entityType);

    public class FilterConvention : IFilterConvention
    {
        private readonly Action<IFilterConventionDescriptor> _configure;
        private FilterConventionDefinition _definition;

        public FilterConvention()
        {
            _configure = Configure;
        }

        public FilterConvention(Action<IFilterConventionDescriptor> descriptor)
        {
            _configure = descriptor;
        }

        public NameString GetArgumentName()
        {
            return GetOrCreateConfiguration().ArgumentName;
        }

        public NameString GetArrayFilterPropertyName()
        {
            return GetOrCreateConfiguration().ElementName;
        }

        public ISet<FilterOperationKind> GetAllowedOperations(FilterFieldDefintion definition)
        {
            if (GetOrCreateConfiguration().TypeDefinitions.TryGetValue(
                definition.Kind,
                out FilterConventionTypeDefinition typeDefinition))
            {
                return typeDefinition.AllowedOperations;
            }
            return new HashSet<FilterOperationKind>();
        }

        public NameString CreateFieldName(
            FilterFieldDefintion definition, FilterOperationKind kind)
        {
            FilterConventionDefinition configuration = GetOrCreateConfiguration();

            if (configuration.TypeDefinitions.TryGetValue(definition.Kind,
                    out FilterConventionTypeDefinition typeDefinition) &&
                    typeDefinition.OperationNames.TryGetValue(kind,
                        out CreateFieldName createFieldName)
                )
            {
                return createFieldName(definition, kind);
            }

            if (configuration.DefaultOperationNames.TryGetValue(kind,
                        out CreateFieldName createDefaultFieldName))
            {
                return createDefaultFieldName(definition, kind);
            }

            throw new SchemaException(
                SchemaErrorBuilder.New()
                .SetMessage(
                    string.Format(
                        FilterResources.FilterConvention_NoOperationNameFound,
                        kind,
                        definition.Kind))
                .SetCode(ErrorCodes.Filtering.NoOperationNameFound)
                .Build());
        }

        public virtual NameString GetFilterTypeName(IDescriptorContext context, Type entityType)
        {
            return GetOrCreateConfiguration().FilterTypeNameFactory(context, entityType);
        }

        public IEnumerable<TryCreateImplicitFilter> GetImplicitFilterFactories()
        {
            return GetOrCreateConfiguration().ImplicitFilters;
        }

        protected virtual void Configure(
            IFilterConventionDescriptor descriptor)
        {
        }

        protected FilterConventionDefinition CreateDefinition()
        {
            var descriptor = FilterConventionDescriptor.New();
            descriptor.UseDefault();
            _configure(descriptor);
            return descriptor.CreateDefinition();
        }

        private FilterConventionDefinition GetOrCreateConfiguration()
        {
            lock (_definition)
            {
                if (_definition == null)
                {
                    _definition = CreateDefinition();
                }
                return _definition;
            }
        }

        public readonly static IFilterConvention Default = new FilterConvention();
    }
}