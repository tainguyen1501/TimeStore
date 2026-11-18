{{/* Chart name, truncated to the 63 characters Kubernetes allows. */}}
{{- define "timestore.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/* Release-qualified name; the release name alone when it already contains the chart name. */}}
{{- define "timestore.fullname" -}}
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

{{- define "timestore.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{ include "timestore.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "timestore.selectorLabels" -}}
app.kubernetes.io/name: {{ include "timestore.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "timestore.image" -}}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) }}
{{- end }}

{{/* The Secret holding the root key pair: the user's, or the chart's own. */}}
{{- define "timestore.rootSecretName" -}}
{{- default (printf "%s-root" (include "timestore.fullname" .)) .Values.rootCredentials.existingSecret }}
{{- end }}

{{/* S3:PublicUrl: the configured value, else the S3 ingress address. */}}
{{- define "timestore.publicUrl" -}}
{{- if .Values.config.publicUrl }}
{{- .Values.config.publicUrl }}
{{- else if .Values.ingress.s3.enabled }}
{{- printf "%s://%s" (ternary "https" "http" (not (empty .Values.ingress.s3.tlsSecret))) .Values.ingress.s3.host }}
{{- end }}
{{- end }}

{{/* Admin:SecureCookies: the configured value, else true when the admin ingress terminates TLS. */}}
{{- define "timestore.secureCookies" -}}
{{- if kindIs "bool" .Values.config.secureCookies }}
{{- .Values.config.secureCookies }}
{{- else }}
{{- and .Values.ingress.admin.enabled (not (empty .Values.ingress.admin.tlsSecret)) }}
{{- end }}
{{- end }}
