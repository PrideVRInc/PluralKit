using System.Text;

using Myriad.Rest.Exceptions;
using Myriad.Rest.Types;
using Myriad.Rest.Types.Requests;
using Myriad.Types;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using PluralKit.Core;

namespace PluralKit.Bot;

public class ImportExport
{
    private readonly HttpClient _client;
    private readonly DataFileService _dataFiles;
    private readonly PrivateChannelService _dmCache;

    private readonly JsonSerializerSettings _settings = new()
    {
        // Otherwise it'll mess up/reformat the ISO strings for ???some??? reason >.>
        DateParseHandling = DateParseHandling.None
    };

    public ImportExport(DataFileService dataFiles, HttpClient client, PrivateChannelService dmCache)
    {
        _dataFiles = dataFiles;
        _client = client;
        _dmCache = dmCache;
    }

    public async Task Import(Context ctx, JObject data)
    {
            

            async Task ConfirmImport(string message)
            {
                var msg = $"{message}\n\nDo you want to proceed with the import?";
                if (!await ctx.PromptYesNo(msg, "Proceed"))
                    throw Errors.ImportCancelled;
            }

            if (data.ContainsKey("accounts")
                && data.Value<JArray>("accounts").Type != JTokenType.Null
                && data.Value<JArray>("accounts").Contains(ctx.Author.Id.ToString()))
            {
                var msg = $"{Emojis.Warn} You seem to importing a system profile belonging to another account. Are you sure you want to proceed?";
                if (!await ctx.PromptYesNo(msg, "Import")) throw Errors.ImportCancelled;
            }

            var result = await _dataFiles.ImportSystem(ctx.Author.Id, ctx.System, data, ConfirmImport);
            if (!result.Success)
                if (result.Message == null)
                    throw Errors.InvalidImportFile;
                else
                    await ctx.Reply(
                        $"{Emojis.Error} The provided system profile could not be imported: {result.Message}");
            else if (ctx.System == null)
                // We didn't have a system prior to importing, so give them the new system's ID
                await ctx.Reply(
                    $"{Emojis.Success} PluralKit has created a system for you based on the given file. Your system ID is `{result.CreatedSystem}`. Type `{ctx.DefaultPrefix}system` for more information.");
            else
                // We already had a system, so show them what changed
                await ctx.Reply(
                    $"{Emojis.Success} Updated {result.Modified} members, created {result.Added} members. Type `{ctx.DefaultPrefix}system list` to check!");
    }

    public async Task Import(Context ctx)
    {
        var inputUrl = ctx.RemainderOrNull() ?? ctx.Message.Attachments.FirstOrDefault()?.Url;
        if (inputUrl == null) throw Errors.NoImportFilePassed;

        if (!Core.MiscUtils.TryMatchUri(inputUrl, out var url))
            throw Errors.InvalidUrl;

        await ctx.BusyIndicator(async () =>
        {
            try
            {
                var response = await _client.GetAsync(url);
                // hacky fix for discord api returning nonsense charsets sometimes
                response.Content.Headers.Remove("content-type");
                response.Content.Headers.Add("content-type", "application/json; charset=UTF-8");
                var content = await response.Content.ReadAsStringAsync();
                if (content == "This content is no longer available.")
                {
                    var refreshed = await ctx.Rest.RefreshUrls(new[] { url.ToString() });
                    response = await _client.GetAsync(new Uri(refreshed.RefreshedUrls[0].Refreshed));
                    content = await response.Content.ReadAsStringAsync();
                }
                if (!response.IsSuccessStatusCode)
                    throw Errors.InvalidImportFile;
                JObject data;
                try
                {
                    data = JsonConvert.DeserializeObject<JObject>(
                        content,
                        _settings
                    );
                    if (data == null)
                        throw Errors.InvalidImportFile;
                }
                catch (InvalidOperationException)
                {
                    // Invalid URL throws this, we just error back out
                    throw Errors.InvalidImportFile;
                }
                catch (JsonException)
                {
                    throw Errors.InvalidImportFile;
                }
                await Import(ctx, data);
            }
            catch (InvalidOperationException)
            {
                // Invalid URL throws this, we just error back out
                throw Errors.InvalidImportFile;
            }
            catch (JsonException)
            {
                throw Errors.InvalidImportFile;
            }
        });
    }
    
    public async Task Sync(Context ctx)
    {
        ctx.CheckSystem();
        
        var token = await ctx.Repository.GetOfficialApiToken(ctx.System.Id);
        if (token == null)
            throw new PKError("You do not have an official API token set. Set one with `pk;officialtoken <token>`. You can get this by DMing `pk;token` to <@466378653216014359>.");
        
        /**
         * .config = /v2/systems/@me/settings
.accounts = [] // no api access
.members = /v2/systems/@me/members
.groups = /v2/systems/@me/groups?with_members=true
.switches = [] // im not parsing that
         */

        await ctx.BusyIndicator(async () => {
            try {
        
                // fetch the following endpoints from api.pluralkit.me
                var configHttpMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.pluralkit.me/v2/systems/@me/settings");
                configHttpMessage.Headers.TryAddWithoutValidation("Authorization", token);
                var configResponse = await _client.SendAsync(configHttpMessage);
                if (!configResponse.IsSuccessStatusCode)
                    throw new PKError("Please check your official API token and try again.");

                var data = new JObject();
                
                var configStr = await configResponse.Content.ReadAsStringAsync();
                try {
                    var config = JsonConvert.DeserializeObject<JObject>(configStr);
                    if (config == null)
                        throw new PKError("Please check your official API token and try again.");
                    data.Add("config", config);
                } catch (JsonException) {
                    throw new PKError("Please check your official API token and try again.");
                }
                
                data.Add("accounts", new JArray());
                
                var membersHttpMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.pluralkit.me/v2/systems/@me/members");
                membersHttpMessage.Headers.TryAddWithoutValidation("Authorization", token);
                var membersResponse = await _client.SendAsync(membersHttpMessage);
                if (!membersResponse.IsSuccessStatusCode)
                    throw new PKError("Please check your official API token and try again.");
                
                var membersStr = await membersResponse.Content.ReadAsStringAsync();
                try {
                    var members = JsonConvert.DeserializeObject<JArray>(membersStr);
                    if (members == null)
                        throw new PKError("Please check your official API token and try again.");
                    data.Add("members", members);
                } catch (JsonException) {
                    throw new PKError("Please check your official API token and try again.");
                }
                
                var groupsHttpMessage = new HttpRequestMessage(HttpMethod.Get, "https://api.pluralkit.me/v2/systems/@me/groups?with_members=true");
                groupsHttpMessage.Headers.TryAddWithoutValidation("Authorization", token);
                var groupsResponse = await _client.SendAsync(groupsHttpMessage);
                if (!groupsResponse.IsSuccessStatusCode)
                    throw new PKError("Please check your official API token and try again.");
                
                var groupsStr = await groupsResponse.Content.ReadAsStringAsync();
                try {
                    var groups = JsonConvert.DeserializeObject<JArray>(groupsStr);
                    if (groups == null)
                        throw new PKError("Please check your official API token and try again.");
                    data.Add("groups", groups);
                } catch (JsonException) {
                    throw new PKError("Please check your official API token and try again.");
                }
                
                data.Add("switches", new JArray());
                
                await Import(ctx, data);
            
            } catch (Exception e) {
                throw new PKError($"An error occurred while syncing your system: {e.Message}");
            }
        });
    }

    public async Task Export(Context ctx)
    {
        ctx.CheckSystem();

        var json = await ctx.BusyIndicator(async () =>
        {
            // Make the actual data file
            var data = await _dataFiles.ExportSystem(ctx.System);
            return JsonConvert.SerializeObject(data, Formatting.None);
        });


        // Send it as a Discord attachment *in DMs*
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        try
        {
            var dm = await _dmCache.GetOrCreateDmChannel(ctx.Author.Id);

            var msg = await ctx.Rest.CreateMessage(dm,
                new MessageRequest { Content = $"{Emojis.Success} Here you go!" },
                new[] { new MultipartFile("system.json", stream, null, null, null) });
            await ctx.Rest.CreateMessage(dm, new MessageRequest { Content = $"<{msg.Attachments[0].Url}>" });

            // If the original message wasn't posted in DMs, send a public reminder
            if (ctx.Channel.Type != Channel.ChannelType.Dm)
                await ctx.Reply($"{Emojis.Success} Check your DMs!");
        }
        catch (ForbiddenException)
        {
            // If user has DMs closed, tell 'em to open them
            await ctx.Reply(
                $"{Emojis.Error} Could not send the data file in your DMs. Do you have DMs closed?");
        }
    }
}