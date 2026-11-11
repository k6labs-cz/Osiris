{{- define "anubis.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "anubis.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{- define "anubis.selectorLabels" -}}
app.kubernetes.io/name: {{ include "anubis.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "anubis.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{ include "anubis.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/* Name of the Secret mounted as Anubis:SecretsDirectory. */}}
{{- define "anubis.secretName" -}}
{{- default (include "anubis.fullname" .) .Values.secrets.existingSecret }}
{{- end }}

{{/* appsettings.json for the ConfigMap; Anubis.Issuer defaults to the first ingress host. */}}
{{- define "anubis.config" -}}
{{- $config := deepCopy (.Values.config | default dict) }}
{{- $anubis := get $config "Anubis" | default dict }}
{{- if and .Values.ingress.enabled (not (get $anubis "Issuer")) .Values.ingress.hosts }}
{{- $_ := set $anubis "Issuer" (printf "https://%s" (first .Values.ingress.hosts)) }}
{{- end }}
{{- $_ := set $config "Anubis" $anubis }}
{{- toPrettyJson $config }}
{{- end }}
