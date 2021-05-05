﻿// Copyright (c) MicroElements. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using MicroElements.Swashbuckle.FluentValidation.Generation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MicroElements.Swashbuckle.FluentValidation
{
    /// <summary>
    /// Swagger <see cref="IOperationFilter"/> that applies FluentValidation rules 
    /// for GET parameters bounded from validatable models.
    /// </summary>
    public class FluentValidationOperationFilter : IOperationFilter
    {
        private readonly ILogger _logger;
        private readonly SwaggerGenOptions _swaggerGenOptions;
        private readonly IValidatorFactory? _validatorFactory;
        private readonly IReadOnlyList<FluentValidationRule> _rules;
        private readonly SchemaGenerationOptions _schemaGenerationOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="FluentValidationOperationFilter"/> class.
        /// </summary>
        /// <param name="swaggerGenOptions">Swagger generation options.</param>
        /// <param name="validatorFactory">FluentValidation factory.</param>
        /// <param name="rules">External FluentValidation rules. External rule overrides default rule with the same name.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        /// <param name="schemaGenerationOptions">Schema generation options.</param>
        public FluentValidationOperationFilter(
            IOptions<SwaggerGenOptions> swaggerGenOptions,
            IValidatorFactory? validatorFactory = null,
            IEnumerable<FluentValidationRule>? rules = null,
            ILoggerFactory? loggerFactory = null,
            IOptions<SchemaGenerationOptions>? schemaGenerationOptions = null)
        {
            _swaggerGenOptions = swaggerGenOptions.Value;
            _validatorFactory = validatorFactory;
            _logger = loggerFactory?.CreateLogger(typeof(FluentValidationRules)) ?? NullLogger.Instance;

            _schemaGenerationOptions = schemaGenerationOptions?.Value ?? new SchemaGenerationOptions();
            _rules = new DefaultFluentValidationRuleProvider(schemaGenerationOptions).GetRules().ToArray().OverrideRules(rules);
        }

        /// <inheritdoc />
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            try
            {
                ApplyInternal(operation, context);
            }
            catch (Exception e)
            {
                var operationId = operation.OperationId ?? context.ApiDescription.RelativePath;
                _logger.LogWarning(0, e, $"Error on apply rules for operation '{operationId}'.");
            }
        }

        private void ApplyInternal(OpenApiOperation operation, OperationFilterContext context)
        {
            if (operation.Parameters == null)
                return;

            if (_validatorFactory == null)
            {
                _logger.LogWarning(0, "ValidatorFactory is not provided. Please register FluentValidation.");
                return;
            }

            var schemaIdSelector = _swaggerGenOptions.SchemaGeneratorOptions.SchemaIdSelector ?? new SchemaGeneratorOptions().SchemaIdSelector;
            var schemaProvider = new SwashbuckleSchemaProvider(context.SchemaRepository, context.SchemaGenerator, schemaIdSelector);

            foreach (var operationParameter in operation.Parameters)
            {
                var apiParameterDescription = context.ApiDescription.ParameterDescriptions.FirstOrDefault(description =>
                    description.Name.Equals(operationParameter.Name, StringComparison.InvariantCultureIgnoreCase));

                var modelMetadata = apiParameterDescription?.ModelMetadata;
                if (modelMetadata != null)
                {
                    var parameterType = modelMetadata.ContainerType;
                    if (parameterType == null)
                        continue;

                    var validator = _validatorFactory.GetValidator(parameterType);
                    if (validator == null)
                        continue;

                    OpenApiSchema schema = schemaProvider.GetSchemaForType(parameterType);

                    if (schema.Properties != null && schema.Properties.Count > 0)
                    {
                        var schemaPropertyName = operationParameter.Name;
                        var apiProperty = schema.Properties.FirstOrDefault(property => property.Key.EqualsIgnoreAll(schemaPropertyName));
                        if (apiProperty.Key != null)
                            schemaPropertyName = apiProperty.Key;

                        var schemaContext = new SchemaGenerationContext
                        {
                            Schema = schema,
                            SchemaType = parameterType,
                            Rules = _rules,
                            SchemaGenerationOptions = _schemaGenerationOptions,
                            ReflectionContext = new ReflectionContext(parameterType, null, null),
                            SchemaProvider = schemaProvider,
                        };

                        FluentValidationSchemaBuilder.ApplyRulesToSchema(
                            schemaType: parameterType,
                            schemaPropertyNames: new[] { schemaPropertyName },
                            validator: validator,
                            logger: _logger,
                            schemaGenerationContext: schemaContext);

                        if (schema.Required != null)
                            operationParameter.Required = schema.Required.Contains(schemaPropertyName, IgnoreAllStringComparer.Instance);

                        var parameterSchema = operationParameter.Schema;
                        if (parameterSchema != null)
                        {
                            if (schema.Properties.TryGetValue(schemaPropertyName.ToLowerCamelCase(), out var property)
                                || schema.Properties.TryGetValue(schemaPropertyName, out property))
                            {
                                // Copy from property schema to parameter schema.
                                parameterSchema.MinLength = property.MinLength;
                                parameterSchema.Nullable = property.Nullable;
                                parameterSchema.MaxLength = property.MaxLength;
                                parameterSchema.Pattern = property.Pattern;
                                parameterSchema.Minimum = property.Minimum;
                                parameterSchema.Maximum = property.Maximum;
                                parameterSchema.ExclusiveMaximum = property.ExclusiveMaximum;
                                parameterSchema.ExclusiveMinimum = property.ExclusiveMinimum;
                                parameterSchema.AllOf = property.AllOf;
                            }
                        }
                    }
                }
            }
        }
    }
}