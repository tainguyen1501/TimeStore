// Amazon S3 bucket naming rules, as the server checks them (backend Domain/Buckets/BucketNames.cs). The server stays
// the authority; this copy lets the form fix and explain a name before it is sent.

export const BUCKET_NAME_MIN = 3
export const BUCKET_NAME_MAX = 63
const RESERVED_PREFIX = "xn--"
const RESERVED_SUFFIX = "-s3alias"

const COMBINING_MARKS = /[̀-ͯ]/g
const SEPARATORS = /[\s_]+/g
const NOT_ALLOWED = /[^a-z0-9.-]/g
const REPEATED_DOTS = /\.{2,}/g
const LETTER_OR_DIGIT = /^[a-z0-9]$/
const IPV4 = /^\d{1,3}(\.\d{1,3}){3}$/

/**
 * Turns what was typed into a name S3 accepts as far as characters go: lowercase, accents removed (á → a, đ → d),
 * spaces and underscores become hyphens, other characters are dropped, repeated dots become one, at most 63
 * characters. Start, end and length are left to {@link bucketNameProblem}, so typing "my-" keeps its hyphen.
 */
export function normalizeBucketName(input: string): string {
  return input
    .toLowerCase()
    .replaceAll("đ", "d")
    .normalize("NFD")
    .replace(COMBINING_MARKS, "")
    .replace(SEPARATORS, "-")
    .replace(NOT_ALLOWED, "")
    .replace(REPEATED_DOTS, ".")
    .slice(0, BUCKET_NAME_MAX)
}

/** Why a (normalized) name is not a valid bucket name, or null when it is. */
export function bucketNameProblem(name: string): string | null {
  if (name.length < BUCKET_NAME_MIN) return `Use at least ${BUCKET_NAME_MIN} characters.`
  if (!LETTER_OR_DIGIT.test(name[0]) || !LETTER_OR_DIGIT.test(name[name.length - 1]))
    return "Start and end with a letter or a digit."
  if (IPV4.test(name)) return "A bucket name cannot look like an IP address."
  if (name.startsWith(RESERVED_PREFIX))
    return `Names starting with "${RESERVED_PREFIX}" are reserved.`
  if (name.endsWith(RESERVED_SUFFIX)) return `Names ending with "${RESERVED_SUFFIX}" are reserved.`
  return null
}
