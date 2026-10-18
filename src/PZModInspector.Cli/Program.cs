using System.Text;
using PZModInspector.Cli;

// UTF-8 also when the output goes to a pipe or a file: names and messages have accents and «».
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
return await Cli.RunAsync(args, Console.Out, Console.Error);
