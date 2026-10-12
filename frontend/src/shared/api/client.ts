import { ApiError, problemDetailsSchema, type ProblemDetails } from "@/shared/api/problem-details";

const configuredBaseUrl = import.meta.env.VITE_API_BASE_URL?.trim() ?? "";
const apiBaseUrl = configuredBaseUrl.replace(/\/$/, "");
const csrfHeaderName = "X-CSRF-TOKEN";
const safeMethods = new Set(["GET", "HEAD", "OPTIONS", "TRACE"]);
let csrfToken: string | undefined;
const unauthorizedListeners = new Set<() => void>();

type RequestBody = BodyInit | Record<string, unknown> | unknown[];

export interface ApiRequestOptions extends Omit<RequestInit, "body" | "headers"> {
  body?: RequestBody;
  headers?: HeadersInit;
  idempotencyKey?: string;
}

function isNativeBody(body: RequestBody): body is BodyInit {
  return (
    typeof body === "string" ||
    body instanceof Blob ||
    body instanceof FormData ||
    body instanceof URLSearchParams ||
    body instanceof ArrayBuffer ||
    ArrayBuffer.isView(body) ||
    body instanceof ReadableStream
  );
}

async function readProblemDetails(response: Response): Promise<ProblemDetails | undefined> {
  const contentType = response.headers.get("content-type") ?? "";

  if (!contentType.includes("json")) {
    return undefined;
  }

  try {
    const parsed = problemDetailsSchema.safeParse(await response.json());
    return parsed.success ? parsed.data : undefined;
  } catch {
    return undefined;
  }
}

export async function apiRequest<TResponse>(
  path: string,
  options: ApiRequestOptions = {},
): Promise<TResponse> {
  if (!path.startsWith("/")) {
    throw new TypeError("API path must start with '/'.");
  }

  const { body, headers: customHeaders, idempotencyKey, ...requestInit } = options;
  const headers = new Headers(customHeaders);
  headers.set("Accept", "application/json");
  const method = requestInit.method?.toUpperCase() ?? "GET";

  if (!safeMethods.has(method) && csrfToken && !headers.has(csrfHeaderName)) {
    headers.set(csrfHeaderName, csrfToken);
  }

  if (idempotencyKey) {
    headers.set("Idempotency-Key", idempotencyKey);
  }

  let requestBody: BodyInit | undefined;

  if (body !== undefined) {
    if (isNativeBody(body)) {
      requestBody = body;
    } else {
      headers.set("Content-Type", "application/json");
      requestBody = JSON.stringify(body);
    }
  }

  const response = await fetch(`${apiBaseUrl}${path}`, {
    ...requestInit,
    body: requestBody,
    credentials: "same-origin",
    headers,
  });

  const responseCsrfToken = response.headers.get(csrfHeaderName)?.trim();
  if (responseCsrfToken) {
    csrfToken = responseCsrfToken;
  }

  if (!response.ok) {
    if (response.status === 401) {
      clearAuthenticatedApiState();
    }
    throw new ApiError(response.status, await readProblemDetails(response));
  }

  if (response.status === 204) {
    return undefined as TResponse;
  }

  return (await response.json()) as TResponse;
}

export function clearApiSessionState() {
  csrfToken = undefined;
}

export function clearAuthenticatedApiState() {
  clearApiSessionState();
  unauthorizedListeners.forEach((listener) => listener());
}

export function subscribeToApiUnauthorized(listener: () => void): () => void {
  unauthorizedListeners.add(listener);
  return () => unauthorizedListeners.delete(listener);
}
