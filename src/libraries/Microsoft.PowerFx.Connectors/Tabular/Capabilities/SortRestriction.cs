﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.PowerFx.Core.Utils;

// DO NOT INCLUDE Microsoft.PowerFx.Core.Functions.Delegation.DelegationMetadata ASSEMBLY
// as it defines CapabilitiesConstants which has invalid values.

namespace Microsoft.PowerFx.Connectors
{
    internal sealed class SortRestriction
    {
        [JsonInclude]
        [JsonPropertyName(CapabilityConstants.AscendingOnlyProperties)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public readonly IList<string> AscendingOnlyProperties;

        [JsonInclude]
        [JsonPropertyName(CapabilityConstants.UnsortableProperties)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public readonly IList<string> UnsortableProperties;

        public SortRestriction(IList<string> unsortableProperties, IList<string> ascendingOnlyProperties)
        {
            Contracts.AssertValueOrNull(unsortableProperties);
            Contracts.AssertValueOrNull(ascendingOnlyProperties);

            // List of properties which support ascending order only
            AscendingOnlyProperties = ascendingOnlyProperties;

            // List of unsortable properties
            UnsortableProperties = unsortableProperties;
        }
    }
}
