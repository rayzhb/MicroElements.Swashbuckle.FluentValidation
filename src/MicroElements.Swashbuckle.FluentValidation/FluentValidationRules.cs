﻿using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Internal;
using FluentValidation.Validators;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MicroElements.Swashbuckle.FluentValidation
{
    /// <summary>
    /// Swagger <see cref="ISchemaFilter"/> that uses FluentValidation validators instead System.ComponentModel based attributes.
    /// </summary>
    public class FluentValidationRules : ISchemaFilter
    {
        private readonly IValidatorFactory _validatorFactory;
        private readonly ILogger _logger;
        private readonly IReadOnlyList<FluentValidationRule> _rules;

        /// <summary>
        /// Initializes a new instance of the <see cref="FluentValidationRules"/> class.
        /// </summary>
        /// <param name="validatorFactory">Validator factory.</param>
        /// <param name="rules">External FluentValidation rules. Rule with the same name replaces default rule.</param>
        /// <param name="loggerFactory"><see cref="ILoggerFactory"/> for logging. Can be null.</param>
        public FluentValidationRules(
            [CanBeNull] IValidatorFactory validatorFactory = null,
            [CanBeNull] IEnumerable<FluentValidationRule> rules = null,
            [CanBeNull] ILoggerFactory loggerFactory = null)
        {
            _validatorFactory = validatorFactory;
            _logger = loggerFactory?.CreateLogger(typeof(FluentValidationRules)) ?? NullLogger.Instance;
            _rules = FluentValidationRulesProvider.Instance.GetRules().OverrideRules(rules);
        }

        /// <inheritdoc />
        public void Apply(OpenApiSchema schema, SchemaFilterContext context)
        {
            if (_validatorFactory == null)
            {
                _logger.LogWarning(0, "ValidatorFactory is not provided. Please register FluentValidation.");
                return;
            }

            IValidator validator = null;
            try
            {       
                validator = _validatorFactory.GetValidator(context.Type);
            }
            catch (Exception e)
            {
                _logger.LogWarning(0, e, $"GetValidator for type '{context.Type}' fails.");
            }

            if (validator == null)
                return;

            ApplyRulesToSchema(schema, context, validator);

            try
            {
                AddRulesFromIncludedValidators(schema, context, validator);
            }
            catch (Exception e)
            {
                _logger.LogWarning(0, e, $"Applying IncludeRules for type '{context.Type}' fails.");
            }
        }

        private void ApplyRulesToSchema(OpenApiSchema schema, SchemaFilterContext context, IValidator validator)
        {
            var lazyLog = new LazyLog(_logger,
                logger => logger.LogDebug($"Applying FluentValidation rules to swagger schema for type '{context.Type}'."));

            foreach (var schemaPropertyName in schema?.Properties?.Keys ?? Array.Empty<string>())
            {
                var validators = validator.GetValidatorsForMemberIgnoreCase(schemaPropertyName);

                foreach (var propertyValidator in validators)
                {
                    foreach (var rule in _rules)
                    {
                        if (rule.Matches(propertyValidator))
                        {
                            try
                            {
                                lazyLog.LogOnce();
                                rule.Apply(new RuleContext(schema, context, schemaPropertyName, propertyValidator));
                                _logger.LogDebug($"Rule '{rule.Name}' applied for property '{context.Type.Name}.{schemaPropertyName}'");
                            }
                            catch (Exception e)
                            {
                                _logger.LogWarning(0, e, $"Error on apply rule '{rule.Name}' for property '{context.Type.Name}.{schemaPropertyName}'.");
                            }
                        }
                    }
                }
            }
        }

        private void AddRulesFromIncludedValidators(OpenApiSchema schema, SchemaFilterContext context, IValidator validator)
        {
            // Note: IValidatorDescriptor doesn't return IncludeRules so we need to get validators manually.
            var childAdapters = (validator as IEnumerable<IValidationRule>)
                .NotNull()
                .Where(rule => rule is IIncludeRule)
                .OfType<PropertyRule>()
                .Where(includeRule => includeRule.HasNoCondition())
                .SelectMany(includeRule => includeRule.Validators)
                .OfType<IChildValidatorAdaptor>();

            foreach (var childAdapter in childAdapters)
            {
                IValidator includedValidator = GetValidatorFromChildValidatorAdapter(childAdapter);
                if (includedValidator != null)
                {
                    ApplyRulesToSchema(schema, context, includedValidator);
                    AddRulesFromIncludedValidators(schema, context, includedValidator);
                }
            }
        }

        //TODO: Independent for Swagger and NSwag
        private IValidator GetValidatorFromChildValidatorAdapter(IChildValidatorAdaptor childValidatorAdapter)
        {
            IValidator validator;
            //if (_validatorFactory is ValidatorFactoryBase instanceCreator)
            //{
            //    validator = instanceCreator.CreateInstance(childValidatorAdapter.ValidatorType);
            //}

            // Fake context. We have not got real context because no validation yet. 
            var fakeContext = new PropertyValidatorContext(new ValidationContext<object>(null), null, string.Empty);

            // Try to validator with reflection. 
            var childValidatorAdapterType = childValidatorAdapter.GetType();
            var getValidatorMethod = childValidatorAdapterType.GetMethod(nameof(ChildValidatorAdaptor<object, object>.GetValidator));
            if (getValidatorMethod != null)
            {
                validator = (IValidator)getValidatorMethod.Invoke(childValidatorAdapter, new[] { fakeContext });
                return validator;
            }

            return null;
        }
    }
}