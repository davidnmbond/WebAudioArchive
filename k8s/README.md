# Kubernetes manifests

Plan 3 Task 22 (issue #110). Today this folder holds the **analysis service only**. The overview and diagram are in
[DESIGN.md](../DESIGN.md#kubernetes-production-no-gpu-nodes); the service itself is described in
[docs/analysis.md](../docs/analysis.md) and [docs/operations.md](../docs/operations.md).

```
k8s/analysis/base/                 namespace agnostic: Deployment, Service, ConfigMap, PVC, NetworkPolicy, ServiceAccount, PriorityClass
k8s/analysis/base/secret.yaml.example   TEMPLATE, placeholders only, not part of the kustomization
k8s/analysis/overlays/example/     what an operator copies and edits: namespace, image, settings, storage class, claim names, GPU server IPs
```

> **WARNING: nothing here may modify or restart the recorders.** The recorders have their own kustomization (or compose
> project), applied on their own, one recorder at a time. Applying, deleting or rolling out anything under `k8s/analysis`
> never touches a recorder: no manifest here defines, patches or selects a recorder workload, and the only things shared
> are two claims of the recordings that analysis mounts **read only**. Never run `kubectl apply -k k8s` on this parent
> folder, never use `kubectl delete -k` with a kustomization that also lists a recorder, and never `--prune` across both.

## Before the first apply

1. **Storage.** Analysis mounts the recordings (the claims the recorders write) **read only**, the analysis claim read and
   write, and the web app cache read only. Several pods mount the same claims at once, so the storage class must offer
   `ReadWriteMany` (NFS, CephFS, Azure Files and so on). With `ReadWriteOnce` claims every pod that mounts one has to run
   on the same node (use `nodeAffinity` or `nodeName` patches in the overlay). The claim names in the base are placeholders:
   `waa-data-a`, `waa-data-b` (the recorders' claims, never created here), `waa-web-cache` (the web app's claim) and
   `waa-data-analysis` (created here). Set the real names, the storage class and the size in your overlay. The merged
   folder (`data-merged`, plan 5) can be added later as another read-only claim and mount.
2. **Make the analysis claim writable by uid 1654** (the user in the pod). There is deliberately no `fsGroup`: Kubernetes
   would recursively change the ownership of every volume that is not mounted read only, and nothing here must be able to
   change the recordings. Prepare the claim once, for example with a throwaway pod or Job that runs `chown 1654:1654 /analysis`
   as root on that claim only, or with the storage class `mountOptions` (NFS: export squash settings).
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

   Re-run it whenever those files change, then `kubectl -n webaudioarchive rollout restart deploy/waa-analysis`.
4. **Settings.** Edit the ConfigMap values in your overlay: `WAA_WHISPER_URL`, `WAA_WHISPER_ALLOWED_HOSTS`,
   `WAA_OLLAMA_URL`, `WAA_OLLAMA_ALLOWED_HOSTS`, `WAA_OLLAMA_MODEL` (pdl-rune-01 has `qwen3.6:27b`), `WAA_MIN_FREE_GB`,
   `WAA_LAG_ALARM_HOURS`. Named hosts must be in the allow-list; private IP addresses are accepted without it. All settings:
   [docs/analysis.md](../docs/analysis.md).
5. **Secrets (optional).** `WAA_WHISPER_TOKEN` and `WAA_OLLAMA_TOKEN` are only needed if a TLS proxy in front of the GPU
   servers wants a bearer token. Create the Secret with the command in `k8s/analysis/base/secret.yaml.example` (never put
   a real value in a file in git). The Deployment marks the Secret optional.
6. **NetworkPolicy.** Egress is limited to cluster DNS and to the GPU server on TCP 8080 (whisper.cpp) and 11434 (Ollama).
   A NetworkPolicy matches addresses, not names, so replace the two placeholder `ipBlock` CIDRs (`192.0.2.10/32` and
   `192.0.2.11/32`, documentation space) with the `/32` of each GPU server (`nslookup pdl-rune-01.panoramicdata.com`), by
   editing `networkpolicy.yaml` or with the patch in the example overlay; delete the second block if you use one server.
   Ingress is allowed only from pods labelled `app.kubernetes.io/name: waa-web` in the same namespace: change the label (and
   add a `namespaceSelector`) to match the web Deployment. If your CNI does not enforce NetworkPolicy, the policy does
   nothing: check with your cluster owner. The DNS rule expects CoreDNS in `kube-system` labelled `k8s-app=kube-dns`.
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

## Reading lag and health

```bash
kubectl -n webaudioarchive port-forward deploy/waa-analysis 8095:8080
curl -s http://127.0.0.1:8095/healthz     # JSON: status, lag hours, whisperReady, paused reason, last error
```

`status` is `ok`, `catching-up` or `paused` (HTTP 200) or `lagging`, `failing`, `stalled` (HTTP 503). Lag is how far behind
real time the oldest closed segment not yet analysed is. A 503 is an alarm for monitoring, **never a reason to restart**, so
the probes are:

| Probe | Check | Why |
|---|---|---|
| startup | TCP on port 8080 | the process is listening; nothing to do with lag |
| liveness | TCP on port 8080 | restarts the pod only when the process is dead or hung; it cannot fail on lag, a paused or unreachable whisper server, or a stalled job |
| readiness | `GET /healthz` | a 503 only removes the pod from the Service; nothing is restarted |

The service has no purely-alive HTTP endpoint yet, so liveness is a TCP check (a follow-up issue asks for `/livez`).

## Un-park or rebuild (maintenance commands)

`--unpark` (put parked jobs back to waiting) and `--rebuild` write the same files as the service and refuse to run while it
is running. Stop the service first, run the command in a one-off Job with the same image and claims, then start it again:

```bash
kubectl -n webaudioarchive scale deploy/waa-analysis --replicas=0
kubectl -n webaudioarchive wait --for=delete pod -l app.kubernetes.io/name=waa-analysis --timeout=120s
# Job: copy the pod spec volumes you need (analysis claim read and write, config) and run the same image with --unpark.
kubectl -n webaudioarchive apply -f unpark-job.yaml        # see the sketch below
kubectl -n webaudioarchive wait --for=condition=complete job/waa-analysis-unpark --timeout=600s
kubectl -n webaudioarchive logs job/waa-analysis-unpark
kubectl -n webaudioarchive delete job waa-analysis-unpark
kubectl -n webaudioarchive scale deploy/waa-analysis --replicas=1
```

Sketch of `unpark-job.yaml` (not applied by anything in this repository; mount the recordings read only and the config
files as the Deployment does, and add them if the command needs them; `--rebuild` is the same with `--rebuild`):

```yaml
apiVersion: batch/v1
kind: Job
metadata:
  name: waa-analysis-unpark
spec:
  backoffLimit: 0
  template:
    spec:
      restartPolicy: Never
      automountServiceAccountToken: false
      securityContext: { runAsNonRoot: true, runAsUser: 1654, runAsGroup: 1654, seccompProfile: { type: RuntimeDefault } }
      containers:
        - name: unpark
          image: registry.example.com/webaudioarchive-analysis:TAG   # the image the Deployment runs
          args: ["--unpark"]
          envFrom: [ { configMapRef: { name: waa-analysis-settings } } ]
          securityContext: { allowPrivilegeEscalation: false, readOnlyRootFilesystem: true, capabilities: { drop: [ALL] } }
          volumeMounts:
            - { name: data-analysis, mountPath: /analysis }
            - { name: config, mountPath: /config, readOnly: true }
            - { name: tmp, mountPath: /tmp }
      volumes:
        - name: data-analysis
          persistentVolumeClaim: { claimName: waa-data-analysis }
        - name: config
          configMap: { name: waa-analysis-files }
        - name: tmp
          emptyDir: {}
```

## Safety properties (checked by `tests/WebAudioArchive.Tests/KubernetesManifestTests.cs`)

One replica with `Recreate`; recordings mounted read only; non-root uid 1654, read-only root filesystem, no privilege
escalation, all capabilities dropped, `RuntimeDefault` seccomp; no `hostPath`, `hostNetwork` or `hostPID`; egress limited
to DNS and two GPU server ports on `/32` addresses; liveness never uses `/healthz`; termination grace of at least 60 s;
the Secret template holds placeholders only; the Service is `ClusterIP`; no manifest mentions a recorder workload.
