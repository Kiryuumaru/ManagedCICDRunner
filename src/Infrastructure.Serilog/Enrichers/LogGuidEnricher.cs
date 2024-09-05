using Application;
using Application.Configuration.Extensions;
using Infrastructure.Serilog.Abstractions;
using Infrastructure.Serilog.Common.LogEventPropertyTypes;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Serilog.Core;
using Serilog.Events;
using System;

namespace Infrastructure.Serilog.Enrichers;

internal class LogGuidEnricher(IConfiguration configuration) : ILogEventEnricher
{
    private readonly IConfiguration _configuration = configuration;

    private const string ValueTypeIdentifier = "ValueType";
    private readonly Dictionary<string, ILogEventPropertyParser> _propertyParserMap = new()
    {
        [BooleanPropertyParser.Default.TypeIdentifier] = BooleanPropertyParser.Default,
        [DateTimeOffsetPropertyParser.Default.TypeIdentifier] = DateTimeOffsetPropertyParser.Default,
        [DateTimePropertyParser.Default.TypeIdentifier] = DateTimePropertyParser.Default,
        [GuidPropertyParser.Default.TypeIdentifier] = GuidPropertyParser.Default,
        [IntPropertyParser.Default.TypeIdentifier] = IntPropertyParser.Default,
        [LongPropertyParser.Default.TypeIdentifier] = LongPropertyParser.Default,
        [ShortPropertyParser.Default.TypeIdentifier] = ShortPropertyParser.Default,
        [StringPropertyParser.Default.TypeIdentifier] = StringPropertyParser.Default,
    };

    private static bool _hasHeadRuntimeLogs = false;
    private static Guid? _runtimeGuid = null;

    public void Enrich(LogEvent evt, ILogEventPropertyFactory _)
    {
        if (_runtimeGuid == null)
        {
            _runtimeGuid = _configuration.GetRuntimeGuid();
        }

        AddProperty(evt, "EventGuid", Guid.NewGuid(), false);
        AddProperty(evt, "RuntimeGuid", _runtimeGuid.Value, false);
        if (!_hasHeadRuntimeLogs)
        {
            AddProperty(evt, "IsHeadLog", true, false);
            _hasHeadRuntimeLogs = true;
        }
        List<LogEventProperty> propsToAdd = [];
        foreach (var prop in evt.Properties)
        {
            propsToAdd.AddRange(GetPropertiesToAdd(evt, prop.Key, prop.Value));
        }
        foreach (var prop in propsToAdd)
        {
            evt.AddOrUpdateProperty(prop);
        }
    }

    private static void AddProperty<T>(LogEvent evt, string key, T value, bool addAndReplace)
    {
        if (key.StartsWith($"{ValueTypeIdentifier}__"))
        {
            return;
        }

        LogEventProperty logEventProperty = new(key, new ScalarValue(value));

        if (addAndReplace)
        {
            evt.AddOrUpdateProperty(logEventProperty);
        }
        else
        {
            evt.AddPropertyIfAbsent(logEventProperty);
        }
    }

    private List<LogEventProperty> GetPropertiesToAdd(LogEvent evt, string key, LogEventPropertyValue valueProp)
    {
        if (key.StartsWith($"{ValueTypeIdentifier}__"))
        {
            return [];
        }

        object? value = (valueProp as ScalarValue)?.Value;
        Type? valueType = value == null ? null : GetUnderlyingType(value);

        if (valueType == null)
        {
            return [];
        }

        LogEventProperty? logEventProperty = null;
        LogEventProperty? logEventIdentifierKey = null;

        string? realValueTypeIdentifierKey = null;

        string valueTypeIdentifierKey = $"{ValueTypeIdentifier}__{key}";
        LogEventPropertyValue? existingTypeIdentifierKeyProp = evt.Properties.GetValueOrDefault(valueTypeIdentifierKey);

        if (existingTypeIdentifierKeyProp != null)
        {
            realValueTypeIdentifierKey = (existingTypeIdentifierKeyProp as ScalarValue)?.Value?.ToString()!;
        }

        realValueTypeIdentifierKey ??= valueType.Name;

        if (realValueTypeIdentifierKey != null && _propertyParserMap.TryGetValue(realValueTypeIdentifierKey, out var logEventPropertyParser))
        {
            if (existingTypeIdentifierKeyProp == null)
            {
                logEventIdentifierKey = new(valueTypeIdentifierKey, new ScalarValue(logEventPropertyParser.TypeIdentifier));
            }
            if (realValueTypeIdentifierKey != valueType.Name)
            {
                logEventProperty = new(key, new ScalarValue(logEventPropertyParser.Parse(value?.ToString())));
            }
        }

        List<LogEventProperty> logEventProperties = [];
        if (logEventProperty != null)
        {
            logEventProperties.Add(logEventProperty);
        }
        if (logEventIdentifierKey != null)
        {
            logEventProperties.Add(logEventIdentifierKey);
        }
        return logEventProperties;
    }

    private static Type GetUnderlyingType(object obj)
    {
        Type valueType = obj.GetType();
        while (true)
        {
            var underlyingType = Nullable.GetUnderlyingType(valueType);
            if (underlyingType == null)
            {
                break;
            }
            valueType = underlyingType;
        }
        return valueType;
    }
}