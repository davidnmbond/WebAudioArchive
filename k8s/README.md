# Kubernetes manifests

Plan 3 Task 22 (issue #110). Today this folder holds the **analysis service only**. The overview and diagram are in
[DESIGN.md](../DESIGN.md#kubernetes-production-no-gpu-nodes); the service itself is described in
[docs/analysis.md](../docs/analysis.md) and [docs/operations.md](../docs/operations.md).

```
k8s/analysis/base/                      namespace agnostic: Deployment, Service, ConfigMap, PVC, NetworkPolicy, ServiceAccount, PriorityClass
k8s/analysis/base/secret.yaml.example   TEMPLATE, placeholders only, not part of the kustomization
k8s/analysis/jobs/*.yaml.example        TEMPLATES for one-off Jobs: prepare-claim (once) and unpark (maintenance)
k8s/analysis/overlays/example/          what an operator copies and edits: namespace, image, settings, storage class, claim names, GPU server IPs
```

> **WARNING: nothing here may modify or restart the recorders.** The recorders have their own kustomization (or compose
> project), applied on their own, one recorder at a time. Applying, deleting or rolling out anything under `k8s/analysis`
> never touches a recorder: no manifest here defines, patches or selects a recorder workload, and the only things shared
> are two claims of the recordings that analysis mounts **read only**. Never run `kubectl apply -k k8s` on this parent
> folder, never use `kubectl delete -k` with a kustomization that also lists a recorder, and never `--prune` across both.

## Before the first apply

1. **Storage and namespace.**
   - Analysis mounts the recordings (the claims the recorders write) **read only**, the analysis claim read and write, and the
     web app cache read only. A claim can only be used by pods in its own namespace, so **analysis must run in the namespace of
     the recorders and the web app** (the overlay sets `namespace:`).
   - The recordings claims must be **`ReadWriteMany`** (NFS, CephFS, Azure Files and so on), or a second `ReadOnlyMany` volume
     on the same export. Do not pin analysis to a node: with `ReadWriteOnce` recordings, an analysis pod that holds the
     volume on the wrong node can block a recorder from starting (Multi-Attach error), which is a gap in the archive. If
     you must use `ReadWriteOnce`: scale analysis to 0 before any recorder is moved, and give the Deployment a **required**
     pod affinity (`requiredDuringSchedulingIgnoredDuringExecution`, topologyKey `kubernetes.io/hostname`) to the recorder
     pods' labels, never a fixed `nodeName` or node selector.
   - The claim names in the base are placeholders: `waa-data-a`, `waa-data-b` (the recordings, never created here),
     `waa-web-cache` (the web app, never created here) and `waa-data-analysis` (created here). Set the real names, the storage
     class and the size in your overlay. The merged folder (`data-merged`, plan 5) can be added later as another read-only claim.
   - Layout: the service expects `<station>/master/...` below each recordings root (`/data/a` and `/data/b`). If the claim holds
     more than one station, or the station folder is not at the claim root, add a `subPath` to that volume mount.
   - **Free space guard.** `WAA_MIN_FREE_GB` (2 here) is checked on the volume that holds the analysis output, so in Kubernetes it
     protects the analysis claim, not the recorders' disk; it must stay well below the claim size (20Gi in `pvc.yaml`) or the
     service pauses for ever. Watch the recorders' storage in their own monitoring.
2. **Make the analysis claim writable by uid 1654** (the user in the pod), once, before the Deployment starts. There is
   deliberately no `fsGroup` on the Deployment: Kubernetes would recursively change the ownership of every volume that is not
   mounted read only. Use `k8s/analysis/jobs/prepare-claim.yaml.example`: it runs `chown 1654:1654 /analysis` as root with only the
   CHOWN capability and mounts only the analysis claim. Without this step the service cannot create its lock file and
   crash-loops. A namespace enforcing Pod Security `restricted` refuses that Job; the file lists the alternatives (an NFS export
   or storage class option that sets the owner, or an initContainer applied once by a cluster admin).
3. **Configuration files.** A kustomization cannot read files outside its own folder, so the files the service reads from
   `/config` are a ConfigMap created from the repository by this command (run from the repository root; it only prints YAML
   until you pipe it into `apply`):

   ```bash
   kubectl -n webaudioarchive create configmap waa-analysis-files \
     --from-file=analysis.json=config/analysis/analysis.json \
     --from-file=swearwords.json=config/analysis/swearwords.json \
     --from-file=enrich-v1.system.md=config/analysis/prompts/enrich-v1.system.md \
     --from-file=enrich-v1.user.md=config/analysis/prompts/enrich-v1.user.md \
     --from-file=enrich-v1.schema.json=config/analysis/prompts/enrich-v1.schema.json \
     --from-file=stations.json=config/stations.json \
     --dry-run=client -o yaml | kubectl -n webaudioarchive apply -f -
   ```

   Re-run it whenever those files change, then `kubectl -n webaudioarchive rollout restart deploy/waa-analysis`. The Deployment's
   `items` map the keys to `/config/analysis/analysis.json` and `/config/stations.json`, the paths the settings name.
4. **Settings.** Edit the ConfigMap values in your overlay: `WAA_WHISPER_URL`, `WAA_WHISPER_ALLOWED_HOSTS`,
   `WAA_OLLAMA_URL`, `WAA_OLLAMA_ALLOWED_HOSTS`, `WAA_OLLAMA_MODEL` (pdl-rune-01 has `qwen3.6:27b`), `WAA_MIN_FREE_GB`,
   `WAA_LAG_ALARM_HOURS`. Named hosts must be in the allow-list; private IP addresses are accepted without it. All settings:
   [docs/analysis.md](../docs/analysis.md). `WAA_SCRATCH_MAX_MB` is 1900 so the scratch stays inside the 2Gi memory-backed
   emptyDir, and `DOTNET_GCHeapHardLimitPercent` is 32 (hexadecimal, 50 percent) so heap plus scratch fit the 6Gi memory limit.
5. **Secrets (optional).** `WAA_WHISPER_TOKEN` and `WAA_OLLAMA_TOKEN` are only needed if a TLS proxy in front of the GPU
   servers wants a bearer token. Put each token in a file that is not in the repository and create the Secret from the files, so
   the value never appears in the shell history or the process list:
   `kubectl -n webaudioarchive create secret generic waa-analysis-secrets --from-file=WAA_WHISPER_TOKEN=./whisper.token --from-file=WAA_OLLAMA_TOKEN=./ollama.token`
   (the key names are the environment variable names). The Deployment marks the Secret optional. `secret.yaml.example` shows the shape.
6. **NetworkPolicy.** Egress is limited to cluster DNS and to the GPU server on TCP 8080 (whisper.cpp) and 11434 (Ollama).
   A NetworkPolicy matches addresses, not names, so replace the placeholder `ipBlock` CIDRs (`192.0.2.10/32` and `192.0.2.11/32`,
   documentation space) with the `/32` of each GPU server (resolve the host names in `WAA_WHISPER_URL` and `WAA_OLLAMA_URL`), by
   editing `networkpolicy.yaml` or with the patch in the example overlay; delete the second block if you use one server.
   Ingress is allowed only from pods labelled `app.kubernetes.io/name: waa-web` in the same namespace: change the label to match
   the web Deployment. If your CNI does not enforce NetworkPolicy, the policy does nothing: check with your cluster owner.
   The DNS rule expects CoreDNS in `kube-system` labelled `k8s-app=kube-dns` and pods that ask the cluster DNS service. With
   **NodeLocal DNSCache** pods ask a link-local address on the node (169.254.20.10) instead, so add an egress rule for that
   address on port 53, or lookups fail and the service cannot resolve the GPU server names.
7. **The PriorityClass** `analysis-low` (value -100, `preemptionPolicy: Never`) is cluster scoped. If you cannot create one,
   remove `priorityclass.yaml` from the base kustomization and `priorityClassName` from the Deployment.
8. **No PodDisruptionBudget** is provided, on purpose: with one replica `minAvailable: 1` would block every node drain for
   ever, and analysis resumes where it stopped, so it may be evicted.

## Build, check, apply

Copy the example overlay (`cp -r k8s/analysis/overlays/example k8s/analysis/overlays/prod`), edit it, then:

```bash
kubectl kustomize k8s/analysis/overlays/prod                       # render only, touches nothing
kubectl kustomize k8s/analysis/overlays/prod | kubectl diff -f -    # what would change in the cluster
kubectl kustomize k8s/analysis/overlays/prod | kubectl apply -f -   # or: kubectl apply -k k8s/analysis/overlays/prod
kubectl -n webaudioarchive rollout status deploy/waa-analysis
kubectl -n webaudioarchive logs deploy/waa-analysis --tail=50
```

Check a manifest offline without a cluster: `kubectl kustomize ... | docker run --rm -i ghcr.io/yannh/kubeconform -strict -summary -`.

Roll out a new image: change `newTag` in the overlay and apply again. The strategy is `Recreate`: the old pod gets 90 s to
stop (a graceful stop finishes or aborts the current chunk and restores the job attempts) and only then the new one
starts, because the service lock refuses a second copy. Roll back with `kubectl -n webaudioarchive rollout undo deploy/waa-analysis`.
Analysis is the only workload these commands name.

**After a hard kill** (node failure, OOM kill, `--grace-period=0`) the lock file is still fresh and the replacement pod has a new
host name, so the service sees "another instance is running" and exits; the pod crash-loops with back-off for up to the lock's
staleness window (5 minutes, `ServiceLock.StaleAfter`) and then starts by itself. That is expected: do not delete the lock by hand.

## Reading lag and health

```bash
kubectl -n webaudioarchive port-forward deploy/waa-analysis 8095:8080
curl -s http://127.0.0.1:8095/healthz     # JSON: status, lag hours, whisperReady, paused reason, last error
```

`status` is `ok`, `catching-up` or `paused` (HTTP 200) or `lagging`, `failing`, `stalled` (HTTP 503). Lag is how far behind
real time the oldest closed segment not yet analysed is. A 503 is an alarm for monitoring, **never a reason to restart or to
hide the pod**, so all three probes are TCP checks on port 8080 (they fail only when the process is not listening). Monitors
and the web app keep reaching the Service, and so the JSON, exactly when the status is bad, and a rollout finishes even when the
GPU server is down. Monitor `/healthz` from outside the probes (a port-forward, a monitoring pod allowed by the NetworkPolicy
ingress, or the web app). The service has no purely-alive HTTP endpoint yet; issue #113 adds `/livez`, after which the probes
can use it.

## Un-park or rebuild (maintenance commands)

`--unpark` (put parked jobs back to waiting) and `--rebuild` write the same files as the service and refuse to run while it
is running. Stop the service first, run the command in a one-off Job with the same image and claims, then start it again:

```bash
cp k8s/analysis/jobs/unpark.yaml.example /tmp/unpark.yaml   # set image, claim names (as in your overlay); --rebuild: change args
kubectl -n webaudioarchive scale deploy/waa-analysis --replicas=0
kubectl -n webaudioarchive wait --for=delete pod -l app.kubernetes.io/name=waa-analysis --timeout=120s
kubectl -n webaudioarchive apply -f /tmp/unpark.yaml
kubectl -n webaudioarchive wait --for=condition=complete job/waa-analysis-unpark --timeout=600s
kubectl -n webaudioarchive logs job/waa-analysis-unpark
kubectl -n webaudioarchive delete job waa-analysis-unpark
kubectl -n webaudioarchive scale deploy/waa-analysis --replicas=1
```

The Job template has the same volumes, config items, environment and security context as the Deployment (recordings read
only, scratch and `/tmp` emptyDirs for the read-only root), and a test keeps them in step. The lock message says "stop the
service first": here that means scale the Deployment to 0.

## Safety properties (checked by `tests/WebAudioArchive.Tests/KubernetesManifestTests.cs`)

One replica with `Recreate`; recordings mounted read only; non-root uid 1654, read-only root filesystem, no privilege
escalation, all capabilities dropped, `RuntimeDefault` seccomp; no `hostPath`, `hostNetwork` or `hostPID`; egress limited
to DNS and two GPU server ports on `/32` addresses; no probe uses `/healthz`; termination grace of at least 60 s; the Secret
template holds placeholders only; the Service is `ClusterIP`; no manifest mentions a recorder workload; the free-space guard is
below the claim size; the Job templates match the Deployment, and the claim preparation Job mounts only the analysis claim with
only the CHOWN capability; the overlay patches volumes by name.
