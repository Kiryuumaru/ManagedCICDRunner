using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Domain.Runner.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunnerStatus
{
    [EnumMember(Value = "building")]
    Building,

    [EnumMember(Value = "starting")]
    Starting,

    [EnumMember(Value = "ready")]
    Ready,

    [EnumMember(Value = "busy")]
    Busy
}
