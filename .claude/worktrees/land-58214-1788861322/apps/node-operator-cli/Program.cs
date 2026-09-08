using Harborline.Api.NodeOperatorCli;

using var client = new HttpClient();
return await OperatorCli.RunAsync(args, client, Console.Out, Console.Error);
