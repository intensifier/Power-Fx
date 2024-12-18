﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.PowerFx.Core.Utils;

// DO NOT INCLUDE Microsoft.PowerFx.Core.Functions.Delegation.DelegationMetadata ASSEMBLY
// as it defines CapabilitiesConstants which has invalid values.

namespace Microsoft.PowerFx.Connectors
{
    internal sealed class GroupRestriction
    {
        [JsonInclude]
        [JsonPropertyName(CapabilityConstants.UngroupableProperties)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public readonly IList<string> UngroupableProperties;

        public GroupRestriction(IList<string> ungroupableProperties)
        {
            Contracts.AssertValueOrNull(ungroupableProperties);

            UngroupableProperties = ungroupableProperties;
        }
    }
}
