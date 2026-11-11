// Writes the template sets Matmon.eu publishes in the template store (store-templates/*.json) and their store texts
// (store-templates/catalog.json). Run: dotnet run store-templates/build.cs   (from the repository root)
// The files are ordinary Matmon template files (Templates > Import reads them too); StoreTemplateFilesTests checks every one
// against the instance's own import rules and - with MATMON_STORE_TEMPLATES_LIVE=1 - runs the status sets against the real pages.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var root = Path.GetDirectoryName(Path.GetFullPath(args.Length > 0 ? args[0] : "store-templates/build.cs"))!;
if (!root.EndsWith("store-templates", StringComparison.Ordinal))
{
    root = Path.Combine(Directory.GetCurrentDirectory(), "store-templates");
}

var catalog = new JsonArray();
var count = 0;

// ------------------------------------------------------------------ helpers

JsonObject Template(string name, string type, string? target, JsonObject parameters, string[] tags,
    JsonObject? thresholds = null, string poll = "00:05:00", string timeout = "00:00:15", string? defaultChannel = null, JsonArray? credentials = null)
{
    var settings = new JsonObject
    {
        ["pollingInterval"] = poll,
        ["timeout"] = timeout,
    };
    if (defaultChannel is not null) settings["defaultChannelKey"] = defaultChannel;
    if (thresholds is not null) settings["thresholds"] = thresholds;
    settings["parameters"] = parameters;
    if (credentials is not null) settings["credentials"] = credentials;
    var template = new JsonObject
    {
        ["id"] = Guid.NewGuid().ToString(),
        ["name"] = name,
        ["targetKind"] = "Sensor",
        ["sensorTypeKey"] = type,
    };
    if (target is not null) template["target"] = target;
    template["settings"] = settings;
    template["tags"] = new JsonArray(tags.Select(tag => (JsonNode)tag).ToArray());
    return template;
}

JsonObject Json(string paths) => new()
{
    ["method"] = "GET",
    ["expectedStatus"] = "200",
    ["extractMode"] = "json",
    ["jsonPaths"] = paths
};

JsonObject Rss() => new()
{
    ["rss.window"] = "24",
    ["rss.statusPage"] = "true",
    ["rss.openDays"] = "14"
};

JsonObject Latency() => new()
{
    ["channel:latency:warning"] = "> 3000",
    ["channel:latency:critical"] = "> 10000"
};

void Set(string slug, string title, string summary, string description, string[] tags, params JsonObject[] templates)
{
    var package = new JsonObject
    {
        ["format"] = "matmon-templates",
        ["version"] = 1,
        ["exportedBy"] = "Matmon.eu",
        ["exportedUtc"] = "2026-11-05T00:00:00+00:00",
        ["templates"] = new JsonArray(templates.Select(t => (JsonNode)t).ToArray())
    };
    File.WriteAllText(Path.Combine(root, slug + ".json"), package.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
    catalog.Add(new JsonObject
    {
        ["file"] = slug + ".json",
        ["title"] = title,
        ["summary"] = summary,
        ["description"] = description,
        ["tags"] = string.Join(", ", tags),
        ["version"] = "1.0",
        ["author"] = "Matmon.eu"
    });
    count++;
}

// An Atlassian Statuspage (the most common hosted status page): the page's own verdict, its components when there are few
// enough to read, and the incident feed.
void Statuspage(string slug, string product, string host, bool components, string summary, string[] extraTags)
{
    var tags = new[] { "status-page", slug.Split('-')[0] };
    var list = new List<JsonObject>
    {
        Template($"{product} - overall status", "http-advanced", $"https://{host}/api/v2/status.json", Json($"status.description|{product}"), tags, Latency())
    };
    if (components)
    {
        list.Add(Template($"{product} - components", "http-advanced", $"https://{host}/api/v2/summary.json", Json("components[*].status|{name}"), tags, Latency()));
    }

    list.Add(Template($"{product} - incidents (RSS)", "rss-feed", $"https://{host}/history.rss", Rss(), tags));
    Set(slug, $"{product} status", summary,
        $"Reads {product}'s public status page ({host}) - no account or credential needed.\n\n"
        + $"\"{product} - overall status\" is the page's own verdict (All Systems Operational / Minor / Major Outage / Maintenance) as one channel"
        + (components ? $"; \"{product} - components\" gives every component its own channel, so a warning names what is affected" : string.Empty)
        + $"; \"{product} - incidents (RSS)\" counts open incidents and maintenance from the page's history feed.\n\n"
        + "Add the templates to a host or folder of your choice and create a sensor from each; the target is already set.",
        ["Status page", .. extraTags], list.ToArray());
}

// ------------------------------------------------------------------ cloud services (public status pages)

Set("microsoft-365-azure-status", "Microsoft 365 & Azure status",
    "Microsoft's public status: Azure, the Microsoft 365 admin center, Microsoft 365 consumer services and Azure incidents.",
    "Reads Microsoft's public status pages (status.cloud.microsoft and azure.status.microsoft) - no account or credential needed.\n\n"
    + "\"Azure - status\" and \"Microsoft 365 - status\" are Microsoft's overall verdicts; \"Microsoft 365 consumer services\" gives every consumer "
    + "service (Outlook.com, OneDrive, Teams free ...) its own channel; \"Azure - incidents (RSS)\" counts open Azure incidents.\n\n"
    + "Note: the health of YOUR tenant's Exchange Online, Teams or SharePoint is only in the Microsoft 365 admin center (Service health) and "
    + "needs a sign-in to Microsoft Graph; these templates show what Microsoft reports publicly - which is what matters when a whole region is down.",
    ["Status page", "Microsoft 365", "Azure", "Exchange Online", "Microsoft"],
    Template("Azure - status", "http-advanced", "https://status.cloud.microsoft/api/posts/azure", Json("Status|Azure"), ["status-page", "microsoft"], Latency()),
    Template("Microsoft 365 - status", "http-advanced", "https://status.cloud.microsoft/api/posts/mac", Json("Status|Microsoft 365"), ["status-page", "microsoft"], Latency()),
    Template("Microsoft 365 consumer services", "http-advanced", "https://status.cloud.microsoft/api/posts/m365Consumer", Json("[*].Status|{ServiceDisplayName}"), ["status-page", "microsoft"], Latency()),
    Template("Azure - incidents (RSS)", "rss-feed", "https://azure.status.microsoft/en-us/status/feed/", Rss(), ["status-page", "microsoft"]));

Set("aws-status", "AWS status",
    "Amazon Web Services' public health feed: open events of every service and region.",
    "Reads the public AWS Health Dashboard feed - no AWS account needed.\n\n"
    + "\"AWS - service health (RSS)\" counts open AWS events (an item closes when AWS marks it resolved). The default feed covers every "
    + "service in every region; to watch only what you use, change the sensor's target to that service's feed, e.g. "
    + "https://status.aws.amazon.com/rss/ec2-eu-central-1.rss (the dashboard lists every feed).",
    ["Status page", "AWS", "Amazon", "Cloud"],
    Template("AWS - service health (RSS)", "rss-feed", "https://status.aws.amazon.com/rss/all.rss", Rss(), ["status-page", "aws"]));

Set("google-cloud-workspace-status", "Google Cloud & Google Workspace status",
    "Google's public incident feeds for Google Cloud and Google Workspace (Gmail, Drive, Meet ...).",
    "Reads Google's public status dashboards - no account needed.\n\n"
    + "\"Google Cloud - incidents (Atom)\" and \"Google Workspace - incidents (Atom)\" count open incidents; an incident closes when Google marks it RESOLVED.",
    ["Status page", "Google Cloud", "Google Workspace", "Gmail", "Google"],
    Template("Google Cloud - incidents (Atom)", "rss-feed", "https://status.cloud.google.com/en/feed.atom", Rss(), ["status-page", "google"]),
    Template("Google Workspace - incidents (Atom)", "rss-feed", "https://www.google.com/appsstatus/dashboard/en/feed.atom", Rss(), ["status-page", "google"]));

Set("hornetsecurity-status", "Hornetsecurity status",
    "Hornetsecurity's public status page: every service, every service per location, and open incidents and maintenance.",
    "Reads Hornetsecurity's public status page (status.io API and its RSS feed) - no account or credential needed.\n\n"
    + "\"Hornetsecurity - services\" gives every service (Spam and Malware Protection, Email Encryption, Backup ...) its own channel; "
    + "\"Hornetsecurity - services by location\" does the same per data centre (\"Spam and Malware Protection - Germany - Frankfurt\"); "
    + "\"Hornetsecurity - incidents (RSS)\" counts open incidents and maintenance. A service that is not operational turns its channel - and "
    + "the sensor - to warning or error, and the message names it.",
    ["Status page", "Hornetsecurity", "E-mail security", "Microsoft 365"],
    Template("Hornetsecurity - services", "http-advanced", "https://api.status.io/1.0/status/591aaa7fe69f388425000fda",
        Json("result.status[*].status|{name}"), ["hornetsecurity", "status-page"], Latency()),
    Template("Hornetsecurity - services by location", "http-advanced", "https://api.status.io/1.0/status/591aaa7fe69f388425000fda",
        Json("result.status[*].containers[*].status|{../name} - {name}"), ["hornetsecurity", "status-page"], Latency()),
    Template("Hornetsecurity - incidents (RSS)", "rss-feed", "https://live.hornet-status.com/pages/591aaa7fe69f388425000fda/rss",
        Rss(), ["hornetsecurity", "status-page"]));

Statuspage("github-status", "GitHub", "www.githubstatus.com", true, "GitHub's public status: overall, every component (Git, Actions, Copilot ...) and incidents.", ["GitHub", "DevOps"]);
Statuspage("cloudflare-status", "Cloudflare", "www.cloudflarestatus.com", false, "Cloudflare's public status: overall verdict and incidents.", ["Cloudflare", "CDN", "DNS"]);
Statuspage("jira-status", "Jira", "jira-software.status.atlassian.com", true, "Jira Cloud's public status: overall, every component and incidents.", ["Atlassian", "Jira"]);
Statuspage("confluence-status", "Confluence", "confluence.status.atlassian.com", true, "Confluence Cloud's public status: overall, every component and incidents.", ["Atlassian", "Confluence"]);
Statuspage("zoom-status", "Zoom", "status.zoom.us", false, "Zoom's public status: overall verdict and incidents.", ["Zoom", "Video conferencing"]);
Statuspage("dropbox-status", "Dropbox", "status.dropbox.com", true, "Dropbox's public status: overall, every component and incidents.", ["Dropbox", "File sharing"]);
Statuspage("teamviewer-status", "TeamViewer", "status.teamviewer.com", true, "TeamViewer's public status: overall, every component and incidents.", ["TeamViewer", "Remote access"]);
Statuspage("anydesk-status", "AnyDesk", "status.anydesk.com", true, "AnyDesk's public status: overall, every component and incidents.", ["AnyDesk", "Remote access"]);
Statuspage("openai-status", "OpenAI", "status.openai.com", true, "OpenAI's public status: overall, every component (ChatGPT, API ...) and incidents.", ["OpenAI", "ChatGPT", "AI"]);
Statuspage("digitalocean-status", "DigitalOcean", "status.digitalocean.com", false, "DigitalOcean's public status: overall verdict and incidents.", ["DigitalOcean", "Cloud"]);
Statuspage("1password-status", "1Password", "status.1password.com", false, "1Password's public status: overall verdict and incidents.", ["1Password", "Passwords"]);

Set("slack-status", "Slack status",
    "Slack's public status: the overall verdict, how many incidents are active, and the incident feed.",
    "Reads Slack's public status API (slack-status.com) - no workspace or token needed.\n\n"
    + "\"Slack - status\" is Slack's overall verdict plus the number of active incidents (a warning from the first one); \"Slack - incidents (RSS)\" counts open incidents and maintenance.",
    ["Status page", "Slack", "Chat"],
    Template("Slack - status", "http-advanced", "https://slack-status.com/api/v2.0.0/current", Json("status|Slack\nactive_incidents|Active incidents"), ["status-page", "slack"],
        new JsonObject { ["channel:latency:warning"] = "> 3000", ["channel:latency:critical"] = "> 10000", ["channel:active_incidents:warning"] = "> 0" }),
    Template("Slack - incidents (RSS)", "rss-feed", "https://slack-status.com/feed/rss", Rss(), ["status-page", "slack"]));

// ------------------------------------------------------------------ Matmon's own services

Set("matmon-status", "Matmon status",
    "Matmon's own services: Matmon Cloud, the matmon.eu website and the download feeds instances and agents update from.",
    "Watches what a Matmon installation relies on - no account or credential needed.\n\n"
    + "\"Matmon Cloud - health\" reads the cloud's health endpoint (cloud.matmon.eu/healthz): its own verdict and response time - "
    + "if it fails, remote access, alert relay, SMS and cloud backups are affected. \"Matmon Cloud - certificate\" warns before "
    + "the cloud's certificate runs out. \"Matmon Cloud - sign-in page\" checks the page people sign in on. \"matmon.eu - website\" "
    + "checks the website. \"Matmon - agent download feed\" and \"Matmon - Windows update feed\" check the release files on GitHub "
    + "that agents and Windows installations update from (how many agent packages there are, the size of the Windows package).\n\n"
    + "Add the templates to a host or folder of your choice and create a sensor from each; the target is already set.",
    ["Status page", "Matmon", "Matmon Cloud"],
    Template("Matmon Cloud - health", "http-advanced", "https://cloud.matmon.eu/healthz", Json("status|Matmon Cloud"), ["status-page", "matmon"], Latency()),
    Template("Matmon Cloud - certificate", "ssl-certificate", "https://cloud.matmon.eu", new JsonObject(), ["status-page", "matmon"], poll: "1.00:00:00"),
    Template("Matmon Cloud - sign-in page", "http-advanced", "https://cloud.matmon.eu/login",
        new JsonObject { ["method"] = "GET", ["expectedStatus"] = "200", ["expectedText"] = "Matmon" }, ["status-page", "matmon"], Latency()),
    Template("matmon.eu - website", "http-advanced", "https://matmon.eu/",
        new JsonObject { ["method"] = "GET", ["expectedStatus"] = "200", ["expectedText"] = "Matmon" }, ["status-page", "matmon"], Latency()),
    Template("Matmon - agent download feed", "http-advanced", "https://github.com/real-ttx/matmon/releases/download/agent-latest/matmon-agent.json",
        Json("packages|Agent packages"), ["status-page", "matmon"],
        new JsonObject { ["channel:latency:warning"] = "> 5000", ["channel:latency:critical"] = "> 15000", ["channel:agent_packages:warning"] = "< 1" },
        poll: "01:00:00"),
    Template("Matmon - Windows update feed", "http-advanced", "https://github.com/real-ttx/matmon/releases/download/windows-latest/matmon-windows-x64.json",
        Json("size|Package size"), ["status-page", "matmon"],
        new JsonObject { ["channel:latency:warning"] = "> 5000", ["channel:latency:critical"] = "> 15000" },
        poll: "01:00:00"));

// ------------------------------------------------------------------ devices (no target: the sensor measures the host it is added to)

// The SNMP basics every device template shares: v2c on 161. The community is a secret and never travels in a template file -
// it comes from the host's SNMP credential or is typed into the sensor.
JsonObject Snmp(string oids) => new()
{
    ["snmp.version"] = "v2c",
    ["snmp.port"] = "161",
    ["snmp.oids"] = oids
};

// UCD-SNMP-MIB (net-snmp's own, and pfSense's "UCD" module): idle CPU, 1-minute load x 100, real memory total / available (kB).
const string UcdSystem = "1.3.6.1.2.1.1.3.0|uptime\n"
    + "1.3.6.1.4.1.2021.11.11.0|cpu_idle\n"
    + "1.3.6.1.4.1.2021.10.1.5.1|load_1min_x100\n"
    + "1.3.6.1.4.1.2021.4.5.0|memory_total_kb\n"
    + "1.3.6.1.4.1.2021.4.6.0|memory_available_kb";

JsonObject CpuIdle() => new()
{
    ["channel:cpu_idle:warning"] = "< 15",
    ["channel:cpu_idle:critical"] = "< 5"
};

Set("pfsense-snmp", "pfSense (SNMP)",
    "pfSense over SNMP: reachability, CPU, load, memory, the packet filter's state table and the WAN interface's traffic.",
    "pfSense has no official API, so these templates read it over SNMP. On the firewall: Services > SNMP - enable it, set a community "
    + "(or use SNMPv3 in the sensor), bind it to the LAN interface and tick the modules MibII, PF and UCD.\n\n"
    + "\"pfSense - ping\" checks that the firewall answers (latency, packet loss). \"pfSense - system (SNMP)\" reads uptime, idle CPU "
    + "(warning below 15 %), the 1-minute load, real memory, whether the packet filter runs (critical when it does not) and the "
    + "number of states. \"pfSense - WAN interface (SNMP)\" shows traffic and utilisation of one interface - set its interface "
    + "index after adding it (index 1 is a guess; the SNMP walk in the sensor editor lists the interfaces). "
    + "\"pfSense - web interface certificate\" warns before the certificate of the web interface runs out.\n\n"
    + "Add the templates to the host that is the firewall; they use the host's address. Put the SNMP community into an SNMP "
    + "credential on the host (or type it into each sensor) - a template file never carries it.",
    ["pfSense", "Firewall", "SNMP", "Netgate"],
    Template("pfSense - ping", "ping", null, new JsonObject { ["count"] = "3" }, ["device", "pfsense"], poll: "00:00:30"),
    Template("pfSense - system (SNMP)", "snmp", null,
        Snmp(UcdSystem
            + "\n1.3.6.1.4.1.12325.1.200.1.1.1.0|pf_running"
            + "\n1.3.6.1.4.1.12325.1.200.1.3.1.0|pf_states"),
        ["device", "pfsense"],
        new JsonObject
        {
            ["channel:cpu_idle:warning"] = "< 15",
            ["channel:cpu_idle:critical"] = "< 5",
            // TruthValue: 1 = true, 2 = false
            ["channel:pf_running:critical"] = "> 1"
        },
        poll: "00:01:00", defaultChannel: "cpu_idle"),
    Template("pfSense - WAN interface (SNMP)", "snmp-interface", null,
        new JsonObject { ["snmp.version"] = "v2c", ["snmp.port"] = "161", ["snmp.interfaceIndex"] = "1" },
        ["device", "pfsense"], poll: "00:01:00"),
    Template("pfSense - web interface certificate", "ssl-certificate", null, new JsonObject(), ["device", "pfsense"], poll: "1.00:00:00"));

Set("starface", "STARFACE",
    "A STARFACE phone system: SIP answers, reachability, the web interface's certificate and - with SNMP switched on - CPU, load and memory.",
    "\"STARFACE - SIP\" sends a SIP OPTIONS request to port 5060 (UDP) and expects any answer - a 401 or 407 (\"log in first\") "
    + "is a healthy answer from a phone system; silence is critical. \"STARFACE - ping\" checks reachability, latency and packet "
    + "loss. \"STARFACE - web interface certificate\" warns before the certificate of the admin interface runs out.\n\n"
    + "\"STARFACE - system (SNMP)\" reads uptime, idle CPU (warning below 15 %), the 1-minute load and memory through the "
    + "UCD-SNMP-MIB of the underlying Linux. It only works where SNMP is switched on for the STARFACE and the probe's address "
    + "is allowed - leave it out otherwise. Use SIP over TCP in the sensor when the system only listens on TCP.\n\n"
    + "Add the templates to the host that is the STARFACE; they use the host's address. Trunk states, registered phones and "
    + "calls are not read - that needs the STARFACE API.",
    ["STARFACE", "Phone system", "VoIP", "SIP"],
    Template("STARFACE - SIP", "sip", null, new JsonObject { ["sip.transport"] = "udp", ["sip.port"] = "5060" }, ["device", "starface"],
        new JsonObject { ["channel:responseTime:warning"] = "> 1000", ["channel:responseTime:critical"] = "> 5000" }, poll: "00:01:00"),
    Template("STARFACE - ping", "ping", null, new JsonObject { ["count"] = "3" }, ["device", "starface"], poll: "00:00:30"),
    Template("STARFACE - web interface certificate", "ssl-certificate", null, new JsonObject(), ["device", "starface"], poll: "1.00:00:00"),
    Template("STARFACE - system (SNMP)", "snmp", null, Snmp(UcdSystem), ["device", "starface"], CpuIdle(),
        poll: "00:05:00", defaultChannel: "cpu_idle"));

// ------------------------------------------------------------------ the catalogue (store texts per file)

File.WriteAllText(Path.Combine(root, "catalog.json"), catalog.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
Console.WriteLine($"{count} sets written to {root}");
