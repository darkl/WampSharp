﻿using System.Threading.Tasks;
using CliFx;

namespace WampSharp.Samples.Publisher
{
    class Program
    {
        static async Task Main(string[] args)
        {
            await new CliApplicationBuilder()
                  .AddCommandsFromThisAssembly()
                  .Build()
                  .RunAsync(args);
        }
    }
}
