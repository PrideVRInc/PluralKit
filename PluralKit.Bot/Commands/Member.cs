using System.Net;
using System.Web;

using Dapper;

using Myriad.Builders;

using Newtonsoft.Json.Linq;

using PluralKit.Core;

namespace PluralKit.Bot;

public class Member
{
    private readonly HttpClient _client;
    private readonly DispatchService _dispatch;
    private readonly EmbedService _embeds;
    private readonly AvatarHostingService _avatarHosting;

    public Member(EmbedService embeds, HttpClient client,
                  DispatchService dispatch, AvatarHostingService avatarHosting)
    {
        _embeds = embeds;
        _client = client;
        _dispatch = dispatch;
        _avatarHosting = avatarHosting;
    }

    public async Task NewMember(Context ctx)
    {
        if (ctx.System == null) throw Errors.NoSystemError(ctx.DefaultPrefix);
        var memberName = ctx.RemainderOrNull() ?? throw new PKSyntaxError("You must pass a member name.");

        // Hard name length cap
        if (memberName.Length > Limits.MaxMemberNameLength)
            throw Errors.StringTooLongError("Member name", memberName.Length, Limits.MaxMemberNameLength);

        // Warn if there's already a member by this name
        var existingMember = await ctx.Repository.GetMemberByName(ctx.System.Id, memberName);
        if (existingMember != null)
        {
            var msg = $"{Emojis.Warn} You already have a member in your system with the name \"{existingMember.NameFor(ctx)}\" (with ID `{existingMember.DisplayHid(ctx.Config)}`). Do you want to create another member with the same name?";
            if (!await ctx.PromptYesNo(msg, "Create")) throw new PKError("Member creation cancelled.");
        }

        await using var conn = await ctx.Database.Obtain();

        // Enforce per-system member limit
        var memberCount = await ctx.Repository.GetSystemMemberCount(ctx.System.Id);
        var memberLimit = ctx.Config.MemberLimitOverride ?? Limits.MaxMemberCount;
        if (memberCount >= memberLimit)
            throw Errors.MemberLimitReachedError(memberLimit);

        // Create the member
        var member = await ctx.Repository.CreateMember(ctx.System.Id, memberName, conn);
        memberCount++;

        JObject dispatchData = new JObject();
        dispatchData.Add("name", memberName);

        if (ctx.Config.MemberDefaultPrivate)
        {
            var patch = new MemberPatch().WithAllPrivacy(PrivacyLevel.Private);
            await ctx.Repository.UpdateMember(member.Id, patch, conn);
            dispatchData.Merge(patch.ToJson());
        }

        // Try to match an image attached to the message
        var avatarArg = ctx.Message.Attachments.FirstOrDefault();
        Exception imageMatchError = null;
        ParsedImage img = new();
        if (avatarArg != null)
            try
            {
                // XXX: discord attachment URLs are unable to be validated without their query params
                // keep both the URL with query (for validation) and the clean URL (for storage) around
                var uriBuilder = new UriBuilder(avatarArg.ProxyUrl);
                img = new ParsedImage { Url = uriBuilder.Uri.AbsoluteUri, Source = AvatarSource.Attachment };

                uriBuilder.Query = "";
                img.CleanUrl = uriBuilder.Uri.AbsoluteUri;

                img = await _avatarHosting.TryRehostImage(img, AvatarHostingService.RehostedImageType.Avatar, ctx.Author.Id, ctx.System);

                await _avatarHosting.VerifyAvatarOrThrow(img.Url);
                await ctx.Repository.UpdateMember(member.Id, new MemberPatch { AvatarUrl = img.CleanUrl ?? img.Url }, conn);

                dispatchData.Add("avatar_url", img.CleanUrl);
            }
            catch (Exception e)
            {
                imageMatchError = e;
            }

        _ = _dispatch.Dispatch(member.Id, new UpdateDispatchData
        {
            Event = DispatchEvent.CREATE_MEMBER,
            EventData = dispatchData,
        });

        // Send confirmation and space hint
        var replyStr = $"{Emojis.Success} Member \"{memberName}\" (`{member.DisplayHid(ctx.Config)}`) registered! Check out the getting started page for how to get a member up and running: https://pluralkit.me/start#create-a-member";
        // todo: move this to ModelRepository
        if (await ctx.Database.Execute(conn => conn.QuerySingleAsync<bool>("select has_private_members(@System)",
                new { System = ctx.System.Id })) && !ctx.Config.MemberDefaultPrivate) //if has private members
            replyStr += $"\n{Emojis.Warn} This member is currently **public**. To change this, use `{ctx.DefaultPrefix}member {member.DisplayHid(ctx.Config)} private`.";
        if (avatarArg != null)
            if (imageMatchError == null)
                replyStr += $"\n{Emojis.Success} Member avatar set to attached image." + (img.Source == AvatarSource.Attachment ? $"\n{Emojis.Warn} If you delete the message containing the attachment, the avatar will stop working." : "");
            else
                replyStr += $"\n{Emojis.Error} Couldn't set avatar: {imageMatchError.Message}";
        if (memberName.Contains(" "))
            replyStr += $"\n{Emojis.Note} Note that this member's name contains spaces. You will need to surround it with \"double quotes\" when using commands referring to it, or just use the member's short ID (which is `{member.DisplayHid(ctx.Config)}`).";
        if (memberCount >= memberLimit)
            replyStr += $"\n{Emojis.Warn} You have reached the per-system member limit ({memberLimit}). If you need to add more members, you can either delete existing members, or ask for your limit to be raised in the PluralKit support server: <https://discord.gg/PczBt78>";
        else if (memberCount >= Limits.WarnThreshold(memberLimit))
            replyStr += $"\n{Emojis.Warn} You are approaching the per-system member limit ({memberCount} / {memberLimit} members). Once you reach this limit, you will be unable to create new members until existing members are deleted, or you can ask for your limit to be raised in the PluralKit support server: <https://discord.gg/PczBt78>";
        await ctx.Reply(replyStr);
    }

    public async Task ViewMember(Context ctx, PKMember target)
    {
        var system = await ctx.Repository.GetSystem(target.System);
        await ctx.Reply(
            embed: await _embeds.CreateMemberEmbed(system, target, ctx.Guild, ctx.Config, ctx.LookupContextFor(system.Id), ctx.Zone));
    }

    public async Task Soulscream(Context ctx, PKMember target)
    {
        // this is for a meme, please don't take this code seriously. :)

        var name = target.NameFor(ctx.LookupContextFor(target.System));
        var encoded = HttpUtility.UrlEncode(name);

        var resp = await _client.GetAsync($"https://onomancer.sibr.dev/api/generateStats2?name={encoded}");
        if (resp.StatusCode != HttpStatusCode.OK)
            // lol
            return;

        var data = JObject.Parse(await resp.Content.ReadAsStringAsync());
        var scream = data["soulscream"]!.Value<string>();

        var eb = new EmbedBuilder()
            .Color(DiscordUtils.Red)
            .Title(name)
            .Url($"https://onomancer.sibr.dev/reflect?name={encoded}")
            .Description($"*{scream}*");
        await ctx.Reply(embed: eb.Build());
    }

    public async Task DisplayId(Context ctx, PKMember target)
    {
        await ctx.Reply(target.DisplayHid(ctx.Config));
    }
}