using Application.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Application;

public static class Defaults
{
    public static AbsolutePath DataPath { get; } = AbsolutePath.Parse(Environment.CurrentDirectory) / ".data";
}
