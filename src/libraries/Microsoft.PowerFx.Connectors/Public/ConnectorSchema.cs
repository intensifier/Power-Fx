﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Diagnostics;
using Microsoft.PowerFx.Types;

namespace Microsoft.PowerFx.Connectors
{
    [DebuggerDisplay("{ConnectorType}")]
    public class ConnectorSchema : SupportsConnectorErrors
    {
        public ConnectorType ConnectorType { get; }

        public FormulaValue DefaultValue { get; }

        internal ISwaggerSchema Schema { get; }

        internal ConnectorExtensions ConnectorExtensions { get; }

        private bool UseHiddenTypes { get; }

        public string Title => Schema.Title;

        public FormulaType FormulaType => UseHiddenTypes ? ConnectorType.HiddenRecordType : ConnectorType.FormulaType;

        internal RecordType HiddenRecordType => ConnectorType.HiddenRecordType;

        public string Summary => ConnectorExtensions.Summary;

        public bool SupportsDynamicIntellisense => ConnectorType.SupportsDynamicIntellisense;

        public bool? NotificationUrl => ConnectorType.NotificationUrl;

        public AiSensitivity AiSensitivity => ConnectorType.AiSensitivity;

        public string PropertyEntityType => ConnectorType.PropertyEntityType;

        internal ConnectorSchema(ISwaggerParameter openApiParameter, ISwaggerExtensions bodyExtensions, bool useHiddenTypes, ConnectorSettings settings)
        {
            Schema = openApiParameter.Schema;
            UseHiddenTypes = useHiddenTypes;
            ConnectorType = AggregateErrorsAndWarnings(openApiParameter.GetConnectorType(settings));
            DefaultValue = openApiParameter.Schema.TryGetDefaultValue(FormulaType, out FormulaValue defaultValue, this) && defaultValue is not BlankValue ? defaultValue : null;
            ConnectorExtensions = new ConnectorExtensions(openApiParameter, bodyExtensions);
        }

        // Intellisense only
        internal ConnectorSchema(ConnectorSchema connectorSchema, ConnectorType connectorType)
        {
            Schema = connectorSchema.Schema;
            DefaultValue = connectorSchema.DefaultValue;
            ConnectorType = AggregateErrorsAndWarnings(connectorType ?? connectorSchema.ConnectorType);
            ConnectorExtensions = connectorSchema.ConnectorExtensions;
            AggregateErrorsAndWarnings(connectorSchema);
            AggregateErrorsAndWarnings(connectorType);
        }
    }
}
