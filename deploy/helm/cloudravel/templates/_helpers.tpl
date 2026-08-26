{{/* Base name (respects nameOverride) */}}
{{- define "cloudravel.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Fully-qualified release name (respects fullnameOverride) */}}
{{- define "cloudravel.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* Common labels */}}
{{- define "cloudravel.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/name: {{ include "cloudravel.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{/* Per-component selector labels: include "cloudravel.selectorLabels" (dict "ctx" . "component" "api") */}}
{{- define "cloudravel.selectorLabels" -}}
app.kubernetes.io/name: {{ include "cloudravel.name" .ctx }}
app.kubernetes.io/instance: {{ .ctx.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{/* Component resource names */}}
{{- define "cloudravel.api.fullname" -}}{{ include "cloudravel.fullname" . }}-api{{- end -}}
{{- define "cloudravel.web.fullname" -}}{{ include "cloudravel.fullname" . }}-web{{- end -}}
{{- define "cloudravel.mssql.fullname" -}}{{ include "cloudravel.fullname" . }}-mssql{{- end -}}
{{- define "cloudravel.azurite.fullname" -}}{{ include "cloudravel.fullname" . }}-azurite{{- end -}}
{{- define "cloudravel.openbao.fullname" -}}{{ include "cloudravel.fullname" . }}-openbao{{- end -}}
{{- define "cloudravel.migrator.fullname" -}}{{ include "cloudravel.fullname" . }}-migrator{{- end -}}

{{/*
ServiceAccount name. One account per release rather than `default`, so the
workloads can be named in RBAC and admission policy. None of the components
talk to the Kubernetes API, hence automountServiceAccountToken: false below.
*/}}
{{- define "cloudravel.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "cloudravel.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/*
Pod-level security context, merged from the chart-wide default and the
component override:
  include "cloudravel.podSecurityContext" (dict "ctx" . "component" "api")
*/}}
{{- define "cloudravel.podSecurityContext" -}}
{{- $default := .ctx.Values.podSecurityContext | default dict -}}
{{- $override := (index .ctx.Values .component).podSecurityContext | default dict -}}
{{- $merged := mergeOverwrite (deepCopy $default) $override -}}
{{- if $merged -}}
{{- toYaml $merged -}}
{{- end -}}
{{- end -}}

{{/*
Container-level security context, same merge.

A note on capabilities: dropping ALL also removes CAP_NET_BIND_SERVICE, and
without it nothing can listen below port 1024 — not even root. The api and web
containers listen on :80, so their values add it back explicitly. Dropping ALL
alone would leave them unable to bind, which surfaces as a crashloop rather
than a permissions error.
*/}}
{{- define "cloudravel.containerSecurityContext" -}}
{{- $default := .ctx.Values.containerSecurityContext | default dict -}}
{{- $override := (index .ctx.Values .component).containerSecurityContext | default dict -}}
{{- $merged := mergeOverwrite (deepCopy $default) $override -}}
{{- if $merged -}}
{{- toYaml $merged -}}
{{- end -}}
{{- end -}}

{{/* Secret name: the managed secret, or the caller-provided existingSecret */}}
{{- define "cloudravel.secretName" -}}
{{- if .Values.secrets.existingSecret -}}
{{- .Values.secrets.existingSecret -}}
{{- else -}}
{{- include "cloudravel.fullname" . }}-secrets
{{- end -}}
{{- end -}}

{{/* Image reference: include "cloudravel.image" (dict "ctx" . "component" "api") */}}
{{- define "cloudravel.image" -}}
{{- $img := index .ctx.Values.image .component -}}
{{- $tag := $img.tag | default .ctx.Chart.AppVersion -}}
{{- printf "%s/%s:%s" .ctx.Values.image.registry $img.repository $tag -}}
{{- end -}}

{{/* --- Derived DB / storage / secret-store targets (bundled vs external) --- */}}

{{- define "cloudravel.dbHost" -}}
{{- if .Values.mssql.enabled -}}
{{ include "cloudravel.mssql.fullname" . }}
{{- else -}}
{{ required "externalMssql.host is required when mssql.enabled=false" .Values.externalMssql.host }}
{{- end -}}
{{- end -}}

{{- define "cloudravel.dbPort" -}}
{{- if .Values.mssql.enabled -}}{{ .Values.mssql.service.port }}{{- else -}}{{ .Values.externalMssql.port }}{{- end -}}
{{- end -}}

{{- define "cloudravel.dbName" -}}
{{- if .Values.mssql.enabled -}}cloudraveldb{{- else -}}{{ .Values.externalMssql.database }}{{- end -}}
{{- end -}}

{{- define "cloudravel.dbUser" -}}
{{- if .Values.mssql.enabled -}}sa{{- else -}}{{ .Values.externalMssql.user }}{{- end -}}
{{- end -}}

{{/* AzureWebJobsStorage: bundled Azurite emulator string, or the external one */}}
{{- define "cloudravel.storageConnectionString" -}}
{{- if .Values.azurite.enabled -}}
{{- $h := include "cloudravel.azurite.fullname" . -}}
{{- printf "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://%s:10000/devstoreaccount1;QueueEndpoint=http://%s:10001/devstoreaccount1;TableEndpoint=http://%s:10002/devstoreaccount1" $h $h $h -}}
{{- else -}}
{{ required "externalStorage.connectionString is required when azurite.enabled=false" .Values.externalStorage.connectionString }}
{{- end -}}
{{- end -}}

{{- define "cloudravel.openBaoAddress" -}}
{{- if .Values.openbao.enabled -}}
{{- printf "http://%s:%v" (include "cloudravel.openbao.fullname" .) .Values.openbao.service.port -}}
{{- else -}}
{{ .Values.externalOpenBao.address }}
{{- end -}}
{{- end -}}
