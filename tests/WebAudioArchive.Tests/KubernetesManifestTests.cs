using System.Text.RegularExpressions;
using WebAudioArchive.Tests.Support;
using YamlDotNet.Serialization;

namespace WebAudioArchive.Tests;

/// <summary>
/// The safety rules of the analysis Kubernetes manifests (plan 3 Task 22), checked on the parsed YAML so every container and every
/// volume is covered and a later edit cannot quietly break them. All scalars are strings (no YAML typing), as in the files.
/// </summary>
public class KubernetesManifestTests
{
    const string Base = "k8s/analysis/base/";
    const string Jobs = "k8s/analysis/jobs/";
    const string Overlay = "k8s/analysis/overlays/example/kustomization.yaml";

    // ---- YAML helpers ----

    static object? Parse(string text) => new DeserializerBuilder().Build().Deserialize<object>(text);

    static object? Doc(string relative) => Parse(RepoFiles.Read(relative));

    static Dictionary<object, object> Map(object? node) => node as Dictionary<object, object> ?? throw new Xunit.Sdk.XunitException("a mapping was expected, got " + (node?.GetType().Name ?? "nothing"));

    static List<object> List(object? node) => node as List<object> ?? throw new Xunit.Sdk.XunitException("a list was expected, got " + (node?.GetType().Name ?? "nothing"));

    /// <summary>Follows keys through mappings; null when any step is missing.</summary>
    static object? At(object? node, params string[] path)
    {
        foreach (var key in path)
        {
            if (node is not Dictionary<object, object> map || !map.TryGetValue(key, out node)) return null;
        }

        return node;
    }

    static string Str(object? node, params string[] path) => At(node, path) as string ?? throw new Xunit.Sdk.XunitException("no scalar at " + string.Join('.', path));

    static IEnumerable<Dictionary<object, object>> Items(object? node) => node is null ? [] : List(node).Select(Map);

    static Dictionary<object, object> PodSpec(object? workload) => Map(At(workload, "spec", "template", "spec"));

    static IEnumerable<Dictionary<object, object>> Containers(object? workload) => Items(At(workload, "spec", "template", "spec", "containers")).Concat(Items(At(workload, "spec", "template", "spec", "initContainers")));

    static Dictionary<string, Dictionary<object, object>> ByName(object? list) => Items(list).ToDictionary(m => (string)m["name"], m => m);

    /// <summary>A stable text form (mappings sorted by key) so two parsed fragments can be compared.</summary>
    static string Canon(object? node) => node switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        Dictionary<object, object> m => "{" + string.Join(",", m.OrderBy(k => (string)k.Key, StringComparer.Ordinal).Select(k => k.Key + ":" + Canon(k.Value))) + "}",
        List<object> l => "[" + string.Join(",", l.Select(Canon)) + "]",
        _ => node.ToString()!,
    };

    static Dictionary<object, object> Deployment => Map(Doc(Base + "deployment.yaml"));

    static Dictionary<object, object> ConfigMap => Map(At(Doc(Base + "configmap.yaml"), "data"));

    static string Code(string relative) => string.Join('\n', RepoFiles.Read(relative).Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    static IEnumerable<string> AllFiles() => Directory
        .EnumerateFiles(Path.Combine(RepoFiles.Root, "k8s", "analysis"), "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(RepoFiles.Root, f).Replace('\\', '/'));

    static IEnumerable<string> ApplicableFiles() => AllFiles().Where(f => f.EndsWith(".yaml", StringComparison.Ordinal));

    static IEnumerable<(string File, Dictionary<object, object> Workload)> PodWorkloads() =>
        new[] { Base + "deployment.yaml", Jobs + "unpark.yaml.example", Jobs + "prepare-claim.yaml.example" }.Select(f => (f, Map(Doc(f))));

    // ---- Deployment shape ----

    [Fact]
    public void Analysis_runs_one_replica_and_recreates_never_rolls()
    {
        var d = Deployment;
        Assert.Equal("1", Str(d, "spec", "replicas"));
        Assert.Equal("Recreate", Str(d, "spec", "strategy", "type"));
        Assert.Null(At(d, "spec", "strategy", "rollingUpdate"));
    }

    [Fact]
    public void Every_container_is_locked_down_and_every_pod_is_non_root_with_seccomp()
    {
        foreach (var (file, w) in PodWorkloads())
        {
            var pod = PodSpec(w);
            Assert.Equal("false", Str(pod, "automountServiceAccountToken"));
            Assert.Equal("RuntimeDefault", Str(pod, "securityContext", "seccompProfile", "type"));
            Assert.Null(At(pod, "securityContext", "fsGroup"));
            foreach (var c in Containers(w))
            {
                var sc = At(c, "securityContext");
                Assert.Equal("false", Str(sc, "allowPrivilegeEscalation"));
                Assert.Equal("true", Str(sc, "readOnlyRootFilesystem"));
                Assert.Equal(["ALL"], List(At(sc, "capabilities", "drop")).Cast<string>());
                Assert.Null(At(sc, "privileged"));
                Assert.NotNull(At(c, "resources", "limits", "memory"));
                Assert.NotNull(At(c, "resources", "limits", "cpu"));
                if (!file.Contains("prepare-claim", StringComparison.Ordinal)) Assert.Null(At(sc, "capabilities", "add"));
            }
        }

        // The analysis pod and the unpark Job run as the same non-root user the image uses.
        foreach (var w in new[] { Deployment, Map(Doc(Jobs + "unpark.yaml.example")) })
        {
            Assert.Equal("true", Str(PodSpec(w), "securityContext", "runAsNonRoot"));
            Assert.Equal("1654", Str(PodSpec(w), "securityContext", "runAsUser"));
        }

        Assert.Equal("false", Str(Doc(Base + "serviceaccount.yaml"), "automountServiceAccountToken"));
    }

    [Fact]
    public void Recordings_are_read_only_by_mount_and_by_claim_and_only_the_analysis_claim_is_written()
    {
        foreach (var w in new[] { Deployment, Map(Doc(Jobs + "unpark.yaml.example")) })
        {
            var volumes = ByName(At(w, "spec", "template", "spec", "volumes"));
            var mounts = Containers(w).Single().TryGetValue("volumeMounts", out var vm) ? Items(vm).ToList() : [];
            var mountOf = mounts.ToDictionary(m => (string)m["name"], m => m);

            foreach (var name in new[] { "data-a", "data-b", "web-cache" })
            {
                Assert.Equal("true", Str(volumes[name], "persistentVolumeClaim", "readOnly"));
                Assert.Equal("true", Str(mountOf[name], "readOnly"));
            }

            Assert.Equal("true", Str(mountOf["config"], "readOnly"));
            Assert.All(mounts.Where(m => ((string)m["mountPath"]).StartsWith("/data", StringComparison.Ordinal)), m => Assert.Equal("true", Str(m, "readOnly")));

            // The only writable mounts: the analysis claim, the scratch and /tmp. The analysis claim is mounted once, at /analysis.
            var writable = mounts.Where(m => !m.ContainsKey("readOnly")).Select(m => (string)m["mountPath"]).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(["/analysis", "/scratch", "/tmp"], writable);
            var analysisMounts = mounts.Where(m => (string)m["name"] == "data-analysis").ToList();
            Assert.Single(analysisMounts);
            Assert.Equal("/analysis", Str(analysisMounts[0], "mountPath"));
            Assert.Equal("waa-data-analysis", Str(volumes["data-analysis"], "persistentVolumeClaim", "claimName"));
            Assert.Null(At(volumes["data-analysis"], "persistentVolumeClaim", "readOnly"));
            Assert.All(volumes.Values, v => Assert.False(v.ContainsKey("hostPath")));
        }
    }

    [Fact]
    public void Scratch_is_a_2Gi_memory_emptydir_with_headroom_and_there_is_a_tmp()
    {
        var volumes = ByName(At(Deployment, "spec", "template", "spec", "volumes"));
        Assert.Equal("Memory", Str(volumes["scratch"], "emptyDir", "medium"));
        Assert.Equal("2Gi", Str(volumes["scratch"], "emptyDir", "sizeLimit"));
        Assert.NotNull(At(volumes["tmp"], "emptyDir"));
        Assert.True(int.Parse(Str(ConfigMap, "WAA_SCRATCH_MAX_MB")) < 2048, "the scratch limit must leave headroom inside the 2Gi emptyDir");
    }

    [Fact]
    public void The_free_space_guard_is_below_the_analysis_claim_size_and_the_gc_fits_the_memory_limit()
    {
        var minFree = int.Parse(Str(ConfigMap, "WAA_MIN_FREE_GB"));
        var size = Str(Doc(Base + "pvc.yaml"), "spec", "resources", "requests", "storage");
        Assert.EndsWith("Gi", size);
        Assert.True(minFree * 2 <= int.Parse(size[..^2]), $"WAA_MIN_FREE_GB {minFree} must be well below the claim size {size}, or the service stays paused");

        // The .NET GC setting is hexadecimal: the heap may use at most half of the container limit, the scratch takes up to 2Gi of it.
        var percent = Convert.ToInt32(Str(ConfigMap, "DOTNET_GCHeapHardLimitPercent"), 16);
        Assert.InRange(percent, 30, 60);
        Assert.Equal("6Gi", Str(Containers(Deployment).Single(), "resources", "limits", "memory"));
        Assert.Equal("250m", Str(Containers(Deployment).Single(), "resources", "requests", "cpu"));
    }

    [Fact]
    public void Nothing_uses_the_host_or_a_gpu()
    {
        foreach (var file in AllFiles())
        {
            var text = Code(file);
            Assert.DoesNotContain("hostPath", text);
            Assert.DoesNotContain("hostNetwork", text);
            Assert.DoesNotContain("hostPID", text);
            Assert.DoesNotContain("hostIPC", text);
            Assert.DoesNotContain("hostPort", text);
            Assert.DoesNotContain("nvidia.com/gpu", text);
        }
    }

    // ---- Probes, grace, priority ----

    [Fact]
    public void No_probe_depends_on_healthz_so_lag_never_restarts_or_hides_the_pod()
    {
        var c = Containers(Deployment).Single();
        foreach (var probe in new[] { "startupProbe", "livenessProbe", "readinessProbe" })
        {
            Assert.NotNull(At(c, probe, "tcpSocket"));
            Assert.Null(At(c, probe, "httpGet"));
            Assert.Null(At(c, probe, "exec"));
        }

        Assert.DoesNotContain("/healthz", Code(Base + "deployment.yaml"));
    }

    [Fact]
    public void Termination_grace_is_long_enough_for_a_graceful_stop_and_the_pod_is_low_priority()
    {
        Assert.True(int.Parse(Str(PodSpec(Deployment), "terminationGracePeriodSeconds")) >= 60, "a graceful stop must finish or abort a chunk and restore the job attempts before the kill");
        Assert.Equal("analysis-low", Str(PodSpec(Deployment), "priorityClassName"));

        var pc = Doc(Base + "priorityclass.yaml");
        Assert.Equal("analysis-low", Str(pc, "metadata", "name"));
        Assert.Equal("Never", Str(pc, "preemptionPolicy"));
        Assert.True(int.Parse(Str(pc, "value")) < 0);
    }

    [Fact]
    public void Every_container_has_explicit_requests_and_limits()
    {
        foreach (var (_, w) in PodWorkloads())
            foreach (var c in Containers(w))
            {
                Assert.NotNull(At(c, "resources", "requests", "cpu"));
                Assert.NotNull(At(c, "resources", "requests", "memory"));
                Assert.NotNull(At(c, "resources", "limits", "cpu"));
                Assert.NotNull(At(c, "resources", "limits", "memory"));
            }
    }

    // ---- Network ----

    [Fact]
    public void Egress_is_limited_to_dns_and_the_gpu_servers_on_two_ports_and_ingress_to_the_web_pods()
    {
        var np = Doc(Base + "networkpolicy.yaml");
        Assert.Equal(["Ingress", "Egress"], List(At(np, "spec", "policyTypes")).Cast<string>());

        var egress = Items(At(np, "spec", "egress")).ToList();
        Assert.All(egress, rule => Assert.True(rule.ContainsKey("to") && rule.ContainsKey("ports"), "a rule without a destination or ports would allow everything"));

        var cidrs = egress.SelectMany(r => Items(r["to"])).Select(t => At(t, "ipBlock", "cidr")).OfType<string>().ToList();
        Assert.NotEmpty(cidrs);
        Assert.All(cidrs, c => Assert.EndsWith("/32", c));
        Assert.DoesNotContain(cidrs, c => c.StartsWith("0.0.0.0", StringComparison.Ordinal));

        var ports = egress.SelectMany(r => Items(r["ports"])).Select(p => int.Parse(Str(p, "port"))).Distinct().Order().ToList();
        Assert.Equal([53, 8080, 11434], ports);

        var ingress = Items(At(np, "spec", "ingress")).ToList();
        Assert.Single(ingress);
        Assert.Equal("waa-web", Str(Items(ingress[0]["from"]).Single(), "podSelector", "matchLabels", "app.kubernetes.io/name"));
        Assert.DoesNotContain("ipBlock", Code(Base + "networkpolicy.yaml").Split("egress:")[0]);
    }

    [Fact]
    public void The_service_is_cluster_ip_and_nothing_is_exposed_outside_the_cluster()
    {
        Assert.Equal("ClusterIP", Str(Doc(Base + "service.yaml"), "spec", "type"));
        foreach (var file in ApplicableFiles())
        {
            var text = Code(file);
            Assert.DoesNotContain("LoadBalancer", text);
            Assert.DoesNotContain("NodePort", text);
            Assert.DoesNotContain("kind: Ingress", text);
            Assert.DoesNotContain("externalIPs", text);
        }
    }

    // ---- Secrets and recorders ----

    [Fact]
    public void The_secret_template_cannot_be_applied_by_accident_and_holds_only_placeholders()
    {
        Assert.Contains(Base + "secret.yaml.example", AllFiles());
        Assert.DoesNotContain(ApplicableFiles(), f => Code(f).Contains("kind: Secret", StringComparison.Ordinal));
        Assert.DoesNotContain("secret", Code(Base + "kustomization.yaml"), StringComparison.OrdinalIgnoreCase);

        var secret = Doc(Base + "secret.yaml.example");
        Assert.Null(At(secret, "data"));
        var values = Map(At(secret, "stringData"));
        Assert.NotEmpty(values);
        Assert.All(values.Values, v => Assert.StartsWith("REPLACE_ME", (string)v));

        // Optional in the pod: the service starts without any token.
        var secretRef = Items(At(Containers(Deployment).Single(), "envFrom")).Single(e => e.ContainsKey("secretRef"));
        Assert.Equal("true", Str(secretRef, "secretRef", "optional"));

        foreach (var file in AllFiles())
        {
            Assert.DoesNotMatch(@"(?i)(token|password|secret|apikey)\w*:\s*[""']?(?!REPLACE_ME)[A-Za-z0-9+/=_-]{24,}", Code(file));
            Assert.DoesNotContain("BEGIN PRIVATE KEY", RepoFiles.Read(file));
        }
    }

    [Fact]
    public void No_manifest_in_the_analysis_folders_names_a_recorder_workload()
    {
        foreach (var file in AllFiles())
        {
            var text = RepoFiles.Read(file);
            Assert.DoesNotMatch(@"(?i)recorder-[ab]\b", text);
            Assert.DoesNotMatch(@"(?i)waa-recorder", text);
            Assert.DoesNotMatch(@"(?im)^\s+name: \S*recorder", text);
        }

        // The recordings are referenced as claims only; the base creates exactly one claim.
        var claims = ApplicableFiles().Where(f => f.StartsWith(Base, StringComparison.Ordinal)).Count(f => Code(f).Contains("kind: PersistentVolumeClaim", StringComparison.Ordinal));
        Assert.Equal(1, claims);
        Assert.Equal("waa-data-analysis", Str(Doc(Base + "pvc.yaml"), "metadata", "name"));
    }

    [Fact]
    public void Base_kustomization_is_namespace_agnostic_and_lists_every_manifest_except_the_templates()
    {
        var k = Map(Doc(Base + "kustomization.yaml"));
        Assert.False(k.ContainsKey("namespace"));
        var resources = List(k["resources"]).Cast<string>().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(ApplicableFiles().Where(f => f.StartsWith(Base, StringComparison.Ordinal) && !f.EndsWith("/kustomization.yaml", StringComparison.Ordinal)).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal), resources);

        var overlay = Map(Doc(Overlay));
        Assert.Equal(["../../base"], List(overlay["resources"]).Cast<string>());
        Assert.True(overlay.ContainsKey("namespace"));
        Assert.True(overlay.ContainsKey("images"));
    }

    // ---- Settings against compose and the config files ----

    static string ComposeValue(string name)
    {
        var line = RepoFiles.Read("docker/docker-compose.analysis.yml").Split('\n').Single(l => l.TrimStart().StartsWith(name + ":", StringComparison.Ordinal));
        var value = line[(line.IndexOf(':') + 1)..].Trim().Trim('"');
        var m = Regex.Match(value, @"^\$\{[A-Z_]+:-(.*)\}$");
        return m.Success ? m.Groups[1].Value : value;
    }

    [Fact]
    public void Settings_values_match_what_compose_sets_for_the_shared_settings()
    {
        // WAA_MIN_FREE_GB and WAA_SCRATCH_MAX_MB differ on purpose (see their own tests): a small claim and a 2Gi memory emptyDir.
        var shared = new[]
        {
            "WAA_STATION", "WAA_RECORDERS", "WAA_ANALYSIS_PREFERENCE", "WAA_ANALYSIS_OUT", "WAA_SCRATCH", "WAA_WHISPER_URL", "WAA_WHISPER_ALLOWED_HOSTS",
            "WAA_WHISPER_MODEL", "WAA_OLLAMA_URL", "WAA_OLLAMA_ALLOWED_HOSTS", "WAA_OLLAMA_MODEL", "WAA_OLLAMA_KEEP_ALIVE", "WAA_ENRICHMENT",
            "WAA_ANALYSIS_CONFIG", "WAA_STATIONS_CONFIG", "WAA_SCHEDULE_FILE", "WAA_LAG_ALARM_HOURS",
        };
        foreach (var name in shared) Assert.Equal(ComposeValue(name), Str(ConfigMap, name));

        Assert.StartsWith("http://", Str(ConfigMap, "WAA_WHISPER_URL"));
        Assert.StartsWith("http://", Str(ConfigMap, "WAA_OLLAMA_URL"));
        Assert.DoesNotContain(ConfigMap.Keys.Cast<string>(), k => k.Contains("TOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public void The_config_items_land_where_the_settings_say_and_exist_in_the_repository()
    {
        var volumes = ByName(At(Deployment, "spec", "template", "spec", "volumes"));
        var mount = Items(At(Containers(Deployment).Single(), "volumeMounts")).Single(m => (string)m["name"] == "config");
        var root = Str(mount, "mountPath");
        var items = Items(At(volumes["config"], "configMap", "items")).ToList();
        var placed = items.Select(i => root + "/" + Str(i, "path")).ToList();

        Assert.Contains(Str(ConfigMap, "WAA_ANALYSIS_CONFIG"), placed);
        Assert.Contains(Str(ConfigMap, "WAA_STATIONS_CONFIG"), placed);
        Assert.Contains(root + "/analysis/swearwords.json", placed);
        Assert.Contains(root + "/analysis/prompts/enrich-v1.system.md", placed);

        // Each placed path is a real file in the repository's config folder, and each key is the file name the README command uses.
        foreach (var i in items)
        {
            Assert.True(File.Exists(Path.Combine(RepoFiles.Root, "config", Str(i, "path").Replace('/', Path.DirectorySeparatorChar))), "config/" + Str(i, "path") + " does not exist");
            Assert.Contains($"--from-file={Str(i, "key")}=config/{Str(i, "path")}", RepoFiles.Read("k8s/README.md"));
        }
    }

    // ---- Job templates ----

    [Fact]
    public void The_unpark_job_has_the_same_volumes_mounts_environment_and_security_context_as_the_deployment()
    {
        var job = Map(Doc(Jobs + "unpark.yaml.example"));
        Assert.Equal("Job", Str(job, "kind"));
        var d = Deployment;
        var jc = Containers(job).Single();
        var dc = Containers(d).Single();

        Assert.Equal(Canon(At(PodSpec(d), "volumes")), Canon(At(PodSpec(job), "volumes")));
        Assert.Equal(Canon(At(dc, "volumeMounts")), Canon(At(jc, "volumeMounts")));
        Assert.Equal(Canon(At(dc, "envFrom")), Canon(At(jc, "envFrom")));
        Assert.Equal(Canon(At(dc, "securityContext")), Canon(At(jc, "securityContext")));
        Assert.Equal(Canon(At(PodSpec(d), "securityContext")), Canon(At(PodSpec(job), "securityContext")));
        Assert.Equal(Str(dc, "image"), Str(jc, "image"));
        Assert.Equal(["--unpark"], List(At(jc, "args")).Cast<string>());
        Assert.Equal("Never", Str(PodSpec(job), "restartPolicy"));
        Assert.Equal("0", Str(job, "spec", "backoffLimit"));
    }

    [Fact]
    public void The_claim_preparation_job_sees_only_the_analysis_claim_and_holds_one_capability()
    {
        var job = Map(Doc(Jobs + "prepare-claim.yaml.example"));
        var pod = PodSpec(job);
        var c = Containers(job).Single();

        Assert.Equal("0", Str(pod, "securityContext", "runAsUser"));
        Assert.Equal(["CHOWN"], List(At(c, "securityContext", "capabilities", "add")).Cast<string>());
        Assert.Equal(["ALL"], List(At(c, "securityContext", "capabilities", "drop")).Cast<string>());
        Assert.Equal("false", Str(c, "securityContext", "allowPrivilegeEscalation"));
        Assert.Equal(["chown", "1654:1654", "/analysis"], List(At(c, "command")).Cast<string>());

        var volumes = Items(At(pod, "volumes")).ToList();
        Assert.Single(volumes);
        Assert.Equal("waa-data-analysis", Str(volumes[0], "persistentVolumeClaim", "claimName"));
        var mounts = Items(At(c, "volumeMounts")).ToList();
        Assert.Single(mounts);
        Assert.Equal("/analysis", Str(mounts[0], "mountPath"));
        Assert.Null(At(c, "envFrom"));
        Assert.Equal("false", Str(pod, "automountServiceAccountToken"));
    }

    // ---- Overlay ----

    [Fact]
    public void The_overlay_patches_volumes_by_name_and_keeps_the_recordings_read_only_and_the_analysis_claim_writable_only_at_analysis()
    {
        var overlay = Map(Doc(Overlay));
        var patches = Items(overlay["patches"]).ToList();
        Assert.NotEmpty(patches);

        foreach (var p in patches)
        {
            // Strategic merge patches only: a JSON patch is a list, and its paths address volumes by position.
            var patch = Parse(Str(p, "patch"));
            Assert.IsType<Dictionary<object, object>>(patch);
            Assert.DoesNotMatch(@"/volumes/\d", Str(p, "patch"));
        }

        var depPatch = Map(Parse(Str(patches.Single(p => Str(p, "target", "kind") == "Deployment"), "patch")));
        var baseVolumes = ByName(At(Deployment, "spec", "template", "spec", "volumes"));
        var patched = Items(At(depPatch, "spec", "template", "spec", "volumes")).ToList();
        Assert.Equal(["data-a", "data-b", "web-cache"], patched.Select(v => (string)v["name"]).Order(StringComparer.Ordinal));
        Assert.Null(At(depPatch, "spec", "template", "spec", "containers"));

        // Merge by name as Kubernetes does: the claim names change, nothing else.
        var merged = baseVolumes.ToDictionary(kv => kv.Key, kv => Map(Parse(RepoFilesYamlOf(kv.Value))));
        foreach (var v in patched)
        {
            var name = (string)v["name"];
            Assert.True(baseVolumes.ContainsKey(name), name + " is not a volume of the base Deployment");
            var claim = Map(At(merged[name], "persistentVolumeClaim"));
            claim["claimName"] = Str(v, "persistentVolumeClaim", "claimName");
        }

        foreach (var name in new[] { "data-a", "data-b", "web-cache" })
        {
            Assert.StartsWith("replace-me-", Str(merged[name], "persistentVolumeClaim", "claimName"));
            Assert.Equal("true", Str(merged[name], "persistentVolumeClaim", "readOnly"));
        }

        Assert.Equal("waa-data-analysis", Str(merged["data-analysis"], "persistentVolumeClaim", "claimName"));
        Assert.Null(At(merged["data-analysis"], "persistentVolumeClaim", "readOnly"));
        var mounts = Items(At(Containers(Deployment).Single(), "volumeMounts")).Where(m => (string)m["name"] == "data-analysis").ToList();
        Assert.Equal(["/analysis"], mounts.Select(m => Str(m, "mountPath")));

        // The network patch keeps the same restrictions.
        var np = Map(Parse(Str(patches.Single(p => Str(p, "target", "kind") == "NetworkPolicy"), "patch")));
        var cidrs = Items(At(np, "spec", "egress")).SelectMany(r => Items(r["to"])).Select(t => At(t, "ipBlock", "cidr")).OfType<string>().ToList();
        Assert.All(cidrs, c => Assert.EndsWith("/32", c));
    }

    /// <summary>Round trip of a parsed node to YAML text (a deep copy that the test can change).</summary>
    static string RepoFilesYamlOf(object node) => new SerializerBuilder().Build().Serialize(node);
}
