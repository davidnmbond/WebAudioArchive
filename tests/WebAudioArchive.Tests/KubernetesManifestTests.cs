using System.Text.RegularExpressions;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>The safety rules of the analysis Kubernetes manifests (plan 3 Task 22), checked on the files so an edit cannot quietly break them.</summary>
public class KubernetesManifestTests
{
    const string Base = "k8s/analysis/base/";

    static string Code(string relative) => string.Join('\n', RepoFiles.Read(relative).Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    static string Deployment => Code(Base + "deployment.yaml");

    /// <summary>The block of lines under a "key:" line, to the next line indented no deeper than the key.</summary>
    static string Block(string text, string keyLine)
    {
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == keyLine);
        Assert.True(start >= 0, $"'{keyLine}' not found");
        var indent = lines[start].Length - lines[start].TrimStart().Length;
        var block = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0 && lines[i].Length - lines[i].TrimStart().Length <= indent && !lines[i].TrimStart().StartsWith('-')) break;
            block.Add(lines[i]);
        }

        return string.Join('\n', block);
    }

    static IEnumerable<string> AllFiles() => Directory
        .EnumerateFiles(Path.Combine(RepoFiles.Root, "k8s", "analysis"), "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(RepoFiles.Root, f).Replace('\\', '/'));

    [Fact]
    public void Analysis_runs_one_replica_and_recreates_never_rolls()
    {
        Assert.Matches(@"(?m)^  replicas: 1$", Deployment);
        Assert.Matches(@"(?m)^  strategy:\n    type: Recreate$", Deployment);
        Assert.DoesNotContain("rollingUpdate", Deployment);
    }

    [Fact]
    public void Recordings_are_mounted_read_only_by_mount_and_by_claim_and_only_analysis_is_writable()
    {
        var d = Deployment;
        foreach (var name in new[] { "data-a", "data-b", "web-cache", "config" })
        {
            Assert.Matches($@"- name: {name}\n\s+mountPath: /\S+\n\s+readOnly: true", d);
        }

        Assert.Matches(@"- name: data-a\n\s+persistentVolumeClaim:\n\s+claimName: \S+\n\s+readOnly: true", d);
        Assert.Matches(@"- name: data-b\n\s+persistentVolumeClaim:\n\s+claimName: \S+\n\s+readOnly: true", d);
        Assert.Matches(@"- name: web-cache\n\s+persistentVolumeClaim:\n\s+claimName: \S+\n\s+readOnly: true", d);

        // Every mount under /data is read only; the only writable mounts are /analysis, /scratch and /tmp.
        var mounts = Block(d, "volumeMounts:");
        var entries = Regex.Matches(mounts, @"- name: (\S+)\n\s+mountPath: (\S+)(\n\s+readOnly: true)?").Select(m => (m.Groups[2].Value, ReadOnly: m.Groups[3].Success)).ToList();
        Assert.Equal(7, entries.Count);
        Assert.All(entries.Where(e => e.Item1.StartsWith("/data", StringComparison.Ordinal)), e => Assert.True(e.ReadOnly, e.Item1));
        Assert.Equal(["/analysis", "/scratch", "/tmp"], entries.Where(e => !e.ReadOnly).Select(e => e.Item1).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Scratch_is_a_memory_emptydir_of_2Gi_and_there_is_a_tmp()
    {
        Assert.Matches(@"- name: scratch\n\s+emptyDir:\n\s+medium: Memory\n\s+sizeLimit: 2Gi", Deployment);
        Assert.Matches(@"- name: tmp\n\s+emptyDir:", Deployment);
    }

    [Fact]
    public void Analysis_is_non_root_with_a_read_only_root_and_no_capabilities()
    {
        var d = Deployment;
        Assert.Contains("runAsNonRoot: true", d);
        Assert.Contains("runAsUser: 1654", d);
        Assert.Contains("readOnlyRootFilesystem: true", d);
        Assert.Contains("allowPrivilegeEscalation: false", d);
        Assert.Matches(@"capabilities:\n\s+drop: \[ALL\]", d);
        Assert.Matches(@"seccompProfile:\n\s+type: RuntimeDefault", d);
        Assert.Matches(@"(?m)^      automountServiceAccountToken: false$", d);
        Assert.DoesNotContain("privileged", d);
        Assert.DoesNotContain("add:", d);
        Assert.DoesNotContain("fsGroup", d);
        Assert.Contains("automountServiceAccountToken: false", Code(Base + "serviceaccount.yaml"));
    }

    [Fact]
    public void Nothing_uses_the_host()
    {
        foreach (var file in AllFiles().Where(f => f.EndsWith(".yaml", StringComparison.Ordinal)))
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

    [Fact]
    public void Egress_is_limited_to_dns_and_the_gpu_servers_on_two_ports_and_ingress_to_the_web_pods()
    {
        var np = Code(Base + "networkpolicy.yaml");
        Assert.Contains("policyTypes: [Ingress, Egress]", np);
        Assert.DoesNotContain("0.0.0.0/0", np);
        Assert.DoesNotContain("::/0", np);

        var cidrs = Regex.Matches(np, @"cidr: (\S+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(cidrs);
        Assert.All(cidrs, c => Assert.EndsWith("/32", c));

        var egress = Block(np, "egress:");
        var ports = Regex.Matches(egress, @"port: (\d+)").Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToList();
        Assert.Equal([53, 8080, 11434], ports);

        // Egress has no rule without a destination, and ingress names the web pods only.
        Assert.Equal(Regex.Matches(egress, @"(?m)^    - to:").Count, Regex.Matches(egress, @"(?m)^    - \S").Count);
        var ingress = Block(np, "ingress:").Split("egress:")[0];
        Assert.Contains("app.kubernetes.io/name: waa-web", ingress);
        Assert.DoesNotContain("ipBlock", ingress);
    }

    [Fact]
    public void Liveness_never_depends_on_healthz_and_readiness_does()
    {
        var d = Deployment;
        var liveness = Block(d, "livenessProbe:").Split("readinessProbe:")[0];
        Assert.Contains("tcpSocket", liveness);
        Assert.DoesNotContain("/healthz", liveness);
        Assert.DoesNotContain("httpGet", liveness);
        Assert.DoesNotContain("exec", liveness);

        var startup = Block(d, "startupProbe:").Split("livenessProbe:")[0];
        Assert.DoesNotContain("/healthz", startup);

        Assert.Matches(@"readinessProbe:\n\s+httpGet:\n\s+path: /healthz", d);
    }

    [Fact]
    public void Termination_grace_is_long_enough_for_a_graceful_stop_and_the_pod_is_low_priority()
    {
        var grace = int.Parse(Regex.Match(Deployment, @"terminationGracePeriodSeconds: (\d+)").Groups[1].Value);
        Assert.True(grace >= 60, "a graceful stop must finish or abort a chunk and restore the job attempts before the kill");
        Assert.Contains("priorityClassName: analysis-low", Deployment);

        var pc = Code(Base + "priorityclass.yaml");
        Assert.Contains("name: analysis-low", pc);
        Assert.Contains("preemptionPolicy: Never", pc);
        Assert.True(int.Parse(Regex.Match(pc, @"(?m)^value: (-?\d+)").Groups[1].Value) < 0);
    }

    [Fact]
    public void Resources_have_explicit_limits_and_no_gpu()
    {
        var d = Deployment;
        Assert.Matches(@"requests:\n\s+cpu: \S+\n\s+memory: \S+", d);
        Assert.Matches(@"limits:\n\s+cpu: \S+\n\s+memory: \S+", d);
    }

    [Fact]
    public void The_service_is_cluster_ip_and_nothing_is_exposed_outside_the_cluster()
    {
        Assert.Contains("type: ClusterIP", Code(Base + "service.yaml"));
        foreach (var file in AllFiles().Where(f => f.EndsWith(".yaml", StringComparison.Ordinal)))
        {
            var text = Code(file);
            Assert.DoesNotContain("LoadBalancer", text);
            Assert.DoesNotContain("NodePort", text);
            Assert.DoesNotContain("kind: Ingress", text);
            Assert.DoesNotContain("externalIPs", text);
        }
    }

    [Fact]
    public void The_secret_template_cannot_be_applied_by_accident_and_holds_only_placeholders()
    {
        Assert.Contains(Base + "secret.yaml.example", AllFiles());
        Assert.DoesNotContain(AllFiles(), f => f.EndsWith(".yaml", StringComparison.Ordinal) && Code(f).Contains("kind: Secret", StringComparison.Ordinal));
        Assert.DoesNotContain("secret", Code(Base + "kustomization.yaml"), StringComparison.OrdinalIgnoreCase);

        var secret = Code(Base + "secret.yaml.example");
        var values = Regex.Matches(secret, @"(?m)^  (WAA_\w+): ""([^""]*)""$").ToList();
        Assert.NotEmpty(values);
        Assert.All(values, m => Assert.StartsWith("REPLACE_ME", m.Groups[2].Value));
        Assert.DoesNotMatch(@"(?m)^data:", secret);

        // Optional in the pod: the service starts without any token.
        Assert.Matches(@"secretRef:\n\s+name: waa-analysis-secrets\n\s+optional: true", Deployment);

        // No token, key or password-looking value in any manifest.
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

        // The recordings are referenced as claims only; this folder creates exactly one claim.
        var claims = AllFiles().Where(f => f.StartsWith(Base, StringComparison.Ordinal) && f.EndsWith(".yaml", StringComparison.Ordinal)).Count(f => Code(f).Contains("kind: PersistentVolumeClaim", StringComparison.Ordinal));
        Assert.Equal(1, claims);
        Assert.Contains("name: waa-data-analysis", Code(Base + "pvc.yaml"));
    }

    [Fact]
    public void Base_kustomization_is_namespace_agnostic_and_lists_every_manifest_except_the_template()
    {
        var k = Code(Base + "kustomization.yaml");
        Assert.DoesNotContain("namespace:", k);
        foreach (var f in new[] { "priorityclass", "serviceaccount", "configmap", "pvc", "deployment", "service", "networkpolicy" })
            Assert.Contains($"- {f}.yaml", k);

        var overlay = Code("k8s/analysis/overlays/example/kustomization.yaml");
        Assert.Contains("- ../../base", overlay);
        Assert.Contains("namespace:", overlay);
        Assert.Contains("images:", overlay);
    }

    [Fact]
    public void Settings_match_what_the_compose_file_sets_and_point_at_the_gpu_servers_by_name_with_an_allow_list()
    {
        var cm = Code(Base + "configmap.yaml");
        var compose = RepoFiles.Read("docker/docker-compose.analysis.yml");
        foreach (var name in new[] { "WAA_RECORDERS", "WAA_ANALYSIS_OUT", "WAA_SCRATCH", "WAA_SCRATCH_MAX_MB", "WAA_MIN_FREE_GB", "WAA_WHISPER_URL", "WAA_WHISPER_ALLOWED_HOSTS", "WAA_WHISPER_MODEL",
                     "WAA_OLLAMA_URL", "WAA_OLLAMA_ALLOWED_HOSTS", "WAA_OLLAMA_MODEL", "WAA_LAG_ALARM_HOURS", "WAA_ANALYSIS_CONFIG", "WAA_STATIONS_CONFIG", "WAA_SCHEDULE_FILE" })
        {
            Assert.Contains($"  {name}:", cm);
            Assert.Contains($"{name}:", compose);
        }

        Assert.Contains("WAA_RECORDERS: a=/data/a,b=/data/b", cm);
        Assert.Contains("WAA_WHISPER_URL: http://", cm);
        Assert.DoesNotContain("https://", cm);
    }
}
