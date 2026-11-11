# Anubis Helm chart

Nasadí Anubis jako jednu repliku (SQLite na persistentním svazku, strategie `Recreate`).

```bash
helm install anubis deploy/helm/anubis -n anubis --create-namespace \
  --set ingress.enabled=true \
  --set ingress.className=nginx \
  --set ingress.hosts[0]=id.example.com \
  --set ingress.tls[0].secretName=anubis-tls \
  --set ingress.tls[0].hosts[0]=id.example.com \
  --set config.Anubis.AdminEmail=admin@example.com
```

| Hodnota | Význam (default) |
| --- | --- |
| `image.repository` / `image.tag` | image (`ghcr.io/k6labs-cz/anubis`, tag = `appVersion` chartu) |
| `config` | konfigurace ve formátu `appsettings.json` (ConfigMap, načítá se znovu při změně); `Anubis.Issuer` se při zapnutém ingressu doplní na `https://<první host>` |
| `secrets.values` | tajné hodnoty jako soubory na klíč, např. `Email__Smtp__Password` |
| `secrets.existingSecret` | použít existující Secret (External Secrets, Sealed Secrets, …) |
| `secrets.generateAdminPassword` | vygenerovat heslo admina, pokud není v `secrets.values` (`true`); zachová se při upgradu |
| `persistence.*` | PVC pro `/data` (`1Gi`, `ReadWriteOnce`); PVC zůstane i po `helm uninstall` |
| `ingress.*` | ingress s TLS (vypnutý) |
| `extraEnv` | další proměnné prostředí (např. `OTEL_EXPORTER_OTLP_ENDPOINT`) |
| `resources`, `podSecurityContext`, `securityContext`, sondy, `nodeSelector`, `tolerations`, `affinity` | jako v `deploy/kubernetes` |

Heslo admina (pokud ho vygeneroval chart):

```bash
kubectl -n anubis get secret anubis -o jsonpath='{.data.Anubis__AdminPassword}' | base64 -d; echo
```
