using System;
using System.Collections.Generic;
using HotChocolate.Language;
using HotChocolate.Types;

namespace HotChocolate.Resolvers
{
    /// <summary>
    /// Represents a field selection during execution.
    /// </summary>
    public interface IFieldSelection
    {
        /// <summary>
        /// Gets the name this field will have in the response map.
        /// </summary>
        NameString ResponseName { get; }

        /// <summary>
        /// Gets the field that was selected.
        /// </summary>
        IObjectField Field { get; }

        /// <summary>
        /// Gets the field selection.
        /// </summary>
        FieldNode Selection { get; }

        /// <summary>
        /// Gets the merged field selections.
        /// </summary>
        IReadOnlyList<FieldNode> Selections { get; }

        /// <summary>
        /// Gets the merged field selections.
        /// </summary>
        [Obsolete("Use Selections.")]
        IReadOnlyList<FieldNode> Nodes { get; }
    }
}
