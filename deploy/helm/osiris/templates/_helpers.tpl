{{- define "osiris.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "osiris.fullname" -}}
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

{{- define "osiris.selectorLabels" -}}
app.kubernetes.io/name: {{ include "osiris.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "osiris.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{ include "osiris.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "osiris.secretName" -}}
{{- default (include "osiris.fullname" .) .Values.secrets.existingSecret }}
{{- end }}

{{/* Anubis's public address: auth.authority, else the bundled Anubis's first ingress host. */}}
{{- define "osiris.authority" -}}
{{- if .Values.auth.authority }}
{{- .Values.auth.authority }}
{{- else if and .Values.anubis.enabled .Values.anubis.ingress.enabled .Values.anubis.ingress.hosts }}
{{- printf "https://%s" (first .Values.anubis.ingress.hosts) }}
{{- else }}
{{- fail "Set auth.authority to the public address of Anubis (or enable anubis.ingress)." }}
{{- end }}
{{- end }}

{{/* A value kept from the Secret of an earlier install, else a new random one. */}}
{{- define "osiris.keep" -}}
{{- $existing := lookup "v1" "Secret" .namespace .secret }}
{{- $current := "" }}
{{- if $existing }}
{{- $current = index ($existing.data | default dict) .key | default "" | b64dec }}
{{- end }}
{{- $current | default .fresh }}
{{- end }}

{{/* The osiris client's secret, the same for Osiris and the bundled Anubis: auth.clientSecret, else generated once. */}}
{{- define "osiris.clientSecret" -}}
{{- .Values.auth.clientSecret | default (include "osiris.keep" (dict "namespace" .Release.Namespace "secret" (include "osiris.fullname" .) "key" "Auth__ClientSecret" "fresh" (randAlphaNum 48))) }}
{{- end }}
