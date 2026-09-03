#!/bin/bash
# Provisions the AWS-side topology into LocalStack. Runs once LocalStack is ready.
#
# Topology:
#
#            Ingest ──┐
#            Catalog ─┼──▶ SNS jamex-video-events ──┬──▶ jamex-encoder-jobs      (Encoder)
#            Encoder ─┘         (one topic)         ├──▶ jamex-catalog-events    (Catalog)
#                                                   ├──▶ jamex-search-events     (Search)
#                                                   └──▶ jamex-engagement-events (Engagement)
#
# Each subscription carries a filter policy, so a queue only receives the event
# types its service actually handles. Without filter policies every consumer
# receives every event and discards most of them — paying for the receive, the
# delete and the wakeup each time.
set -euo pipefail

REGION="${AWS_DEFAULT_REGION:-us-east-1}"
RAW_BUCKET="jamex-raw"
MEDIA_BUCKET="jamex-media"
TOPIC="jamex-video-events"

echo "[jamex] provisioning local AWS in ${REGION}"

# ---------------------------------------------------------------------------
# S3 — "upload storage" (transient raw) and "blob storage" (encoded output).
# ---------------------------------------------------------------------------
awslocal s3api create-bucket --bucket "${RAW_BUCKET}"   --region "${REGION}" >/dev/null
awslocal s3api create-bucket --bucket "${MEDIA_BUCKET}" --region "${REGION}" >/dev/null

# The browser PUTs parts straight to S3 with presigned URLs, so it must be able
# to read the ETag back off its own response — completing a multipart upload
# requires echoing the ETag of every part. Omit this and uploads can never
# finish, with no useful error to explain why.
#
# app.localstack.cloud is included so the Resource Browser (a page on that
# origin, running in your own browser) can list and preview objects in this
# bucket. Without it, opening jamex-raw there fails with an opaque "network
# failure" — CORS blocked the request client-side and no error surfaces from
# LocalStack's side to explain why.
awslocal s3api put-bucket-cors --bucket "${RAW_BUCKET}" --cors-configuration '{
  "CORSRules": [{
    "AllowedOrigins": ["http://localhost:3000", "http://localhost:3100", "http://localhost:8080", "https://app.localstack.cloud"],
    "AllowedMethods": ["PUT", "POST", "GET", "HEAD", "DELETE"],
    "AllowedHeaders": ["*"],
    "ExposeHeaders": ["ETag", "x-amz-request-id"],
    "MaxAgeSeconds": 3000
  }]
}'

awslocal s3api put-bucket-cors --bucket "${MEDIA_BUCKET}" --cors-configuration '{
  "CORSRules": [{
    "AllowedOrigins": ["*"],
    "AllowedMethods": ["GET", "HEAD"],
    "AllowedHeaders": ["*"],
    "ExposeHeaders": ["ETag", "Content-Length", "Content-Range"],
    "MaxAgeSeconds": 3000
  }]
}'

# Parts of an abandoned multipart upload are invisible to ListObjects but still
# billed, forever. At YouTube ingest rates that is an unbounded silent cost.
awslocal s3api put-bucket-lifecycle-configuration --bucket "${RAW_BUCKET}" --lifecycle-configuration '{
  "Rules": [
    {
      "ID": "abandoned-multipart-uploads",
      "Status": "Enabled",
      "Filter": {"Prefix": ""},
      "AbortIncompleteMultipartUpload": {"DaysAfterInitiation": 1}
    },
    {
      "ID": "expire-raw-after-encode",
      "Status": "Enabled",
      "Filter": {"Prefix": "uploads/"},
      "Expiration": {"Days": 30}
    }
  ]
}'

echo "[jamex] s3 ready: ${RAW_BUCKET}, ${MEDIA_BUCKET}"

# ---------------------------------------------------------------------------
# SNS — one topic for every video lifecycle event.
# ---------------------------------------------------------------------------
TOPIC_ARN=$(awslocal sns create-topic --name "${TOPIC}" --output text --query TopicArn)
echo "[jamex] sns topic: ${TOPIC_ARN}"

queue_url() {
  awslocal sqs get-queue-url --queue-name "$1" --output text --query QueueUrl
}

queue_arn() {
  awslocal sqs get-queue-attributes \
    --queue-url "$1" --attribute-names QueueArn \
    --output text --query 'Attributes.QueueArn'
}

# Escapes a JSON document so it can be embedded as a JSON *string* value.
# Several AWS attributes (RedrivePolicy, FilterPolicy, Policy) are declared as
# strings that happen to contain JSON, which is the source of most of the
# quoting pain in scripts like this. Building the attribute files on disk and
# passing file:// keeps the escaping in one place.
json_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

# Creates <name> plus <name>-dlq, wires redrive, grants SNS delivery rights,
# and subscribes the queue to the topic with a filter policy.
#   $1 queue name   $2 visibility timeout (s)   $3 comma-separated event types
create_subscribed_queue() {
  local name="$1" visibility="$2" events="$3"
  local dlq="${name}-dlq"

  awslocal sqs create-queue --queue-name "${dlq}" >/dev/null
  local dlq_arn
  dlq_arn=$(queue_arn "$(queue_url "${dlq}")")

  local redrive
  redrive=$(json_escape "{\"deadLetterTargetArn\":\"${dlq_arn}\",\"maxReceiveCount\":\"3\"}")

  cat > /tmp/queue-attrs.json <<EOF
{
  "VisibilityTimeout": "${visibility}",
  "MessageRetentionPeriod": "345600",
  "ReceiveMessageWaitTimeSeconds": "20",
  "RedrivePolicy": "${redrive}"
}
EOF
  awslocal sqs create-queue --queue-name "${name}" \
    --attributes file:///tmp/queue-attrs.json >/dev/null

  local url arn
  url=$(queue_url "${name}")
  arn=$(queue_arn "${url}")

  # SNS may only deliver to a queue that grants it sqs:SendMessage, and the
  # condition scopes that grant to this topic so no other topic can publish in.
  local policy
  policy=$(json_escape "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"Service\":\"sns.amazonaws.com\"},\"Action\":\"sqs:SendMessage\",\"Resource\":\"${arn}\",\"Condition\":{\"ArnEquals\":{\"aws:SourceArn\":\"${TOPIC_ARN}\"}}}]}")

  cat > /tmp/queue-policy.json <<EOF
{ "Policy": "${policy}" }
EOF
  awslocal sqs set-queue-attributes --queue-url "${url}" \
    --attributes file:///tmp/queue-policy.json >/dev/null

  # Turn VideoEncoded,VideoDeleted into ["VideoEncoded","VideoDeleted"].
  local quoted
  quoted=$(printf '%s' "${events}" | sed 's/[^,][^,]*/"&"/g')
  local filter
  filter=$(json_escape "{\"eventType\":[${quoted}]}")

  # RawMessageDelivery=true makes the SQS body the published message itself
  # rather than an SNS notification wrapper, and passes message attributes
  # through. Consumers then read one shape regardless of how a message arrived.
  cat > /tmp/subscription-attrs.json <<EOF
{
  "RawMessageDelivery": "true",
  "FilterPolicy": "${filter}"
}
EOF
  awslocal sns subscribe \
    --topic-arn "${TOPIC_ARN}" \
    --protocol sqs \
    --notification-endpoint "${arn}" \
    --attributes file:///tmp/subscription-attrs.json >/dev/null

  echo "[jamex]   ${name} (dlq after 3, visibility ${visibility}s) <- ${events}"
}

# Encoder: only cares that a new raw file exists. Long visibility because
# transcoding is slow; the consumer extends it by heartbeat while it works.
create_subscribed_queue "jamex-encoder-jobs" 900 "VideoUploaded"

# Catalog: follows the whole lifecycle so it can show the uploader real status
# rather than leaving a failed video stuck in Transcoding forever.
create_subscribed_queue "jamex-catalog-events" 60 \
  "VideoUploaded,VideoEncoded,VideoEncodingFailed"

# Search: indexes only once a video is playable, removes it when deleted.
# Nothing unwatchable should be discoverable.
create_subscribed_queue "jamex-search-events" 60 "VideoEncoded,VideoDeleted"

# Engagement: initialises counters when a video becomes playable.
create_subscribed_queue "jamex-engagement-events" 60 "VideoEncoded,VideoDeleted"

rm -f /tmp/queue-attrs.json /tmp/queue-policy.json /tmp/subscription-attrs.json

echo "[jamex] sqs ready: 4 queues + 4 dlqs, all subscribed with filter policies"

# ---------------------------------------------------------------------------
# DynamoDB — the doc's Bigtable: high-throughput key-value data that would
# crush the relational tier. Each table is owned by exactly one service.
# ---------------------------------------------------------------------------

# Owner: Engagement. Sharded counters — views on a viral video are the hottest
# write in the system, and a single item caps out around a partition's write
# ceiling. Writes fan out over VIEWS#0..N and reads sum the shards.
awslocal dynamodb create-table \
  --table-name jamex-video-counters \
  --attribute-definitions AttributeName=videoId,AttributeType=S AttributeName=counterKey,AttributeType=S \
  --key-schema AttributeName=videoId,KeyType=HASH AttributeName=counterKey,KeyType=RANGE \
  --billing-mode PAY_PER_REQUEST >/dev/null

# Owner: Engagement. One row per (user, video) makes like/dislike idempotent —
# the counter delta is computed from the transition, not from the request.
awslocal dynamodb create-table \
  --table-name jamex-user-reactions \
  --attribute-definitions AttributeName=userId,AttributeType=S AttributeName=videoId,AttributeType=S \
  --key-schema AttributeName=userId,KeyType=HASH AttributeName=videoId,KeyType=RANGE \
  --global-secondary-indexes '[{
    "IndexName": "by-video",
    "KeySchema": [
      {"AttributeName": "videoId", "KeyType": "HASH"},
      {"AttributeName": "userId",  "KeyType": "RANGE"}
    ],
    "Projection": {"ProjectionType": "ALL"}
  }]' \
  --billing-mode PAY_PER_REQUEST >/dev/null

# Owner: Catalog. The doc's headline Bigtable case: many small records per
# video, enormous read volume. Images live in S3; only references live here.
awslocal dynamodb create-table \
  --table-name jamex-thumbnails \
  --attribute-definitions AttributeName=videoId,AttributeType=S AttributeName=thumbnailId,AttributeType=S \
  --key-schema AttributeName=videoId,KeyType=HASH AttributeName=thumbnailId,KeyType=RANGE \
  --billing-mode PAY_PER_REQUEST >/dev/null

# Owner: Search. Inverted index exactly as chapter 3 describes: key is the
# term, value carries frequency and which field matched.
awslocal dynamodb create-table \
  --table-name jamex-search-index \
  --attribute-definitions AttributeName=term,AttributeType=S AttributeName=videoId,AttributeType=S \
  --key-schema AttributeName=term,KeyType=HASH AttributeName=videoId,KeyType=RANGE \
  --billing-mode PAY_PER_REQUEST >/dev/null

# Owner: Ingest. Which parts of a multipart upload have landed, so a dropped
# connection resumes instead of restarting. TTL reaps abandoned sessions, which
# pairs with the bucket's AbortIncompleteMultipartUpload rule reaping the bytes.
awslocal dynamodb create-table \
  --table-name jamex-upload-sessions \
  --attribute-definitions AttributeName=uploadId,AttributeType=S \
  --key-schema AttributeName=uploadId,KeyType=HASH \
  --billing-mode PAY_PER_REQUEST >/dev/null

awslocal dynamodb update-time-to-live \
  --table-name jamex-upload-sessions \
  --time-to-live-specification "Enabled=true,AttributeName=expiresAt" >/dev/null

echo "[jamex] dynamodb ready: counters, reactions, thumbnails, search-index, upload-sessions"
echo "[jamex] bootstrap complete"
