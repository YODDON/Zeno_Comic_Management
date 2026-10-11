import { z } from "zod";

export const problemDetailsSchema = z
  .object({
    type: z.string().optional(),
    title: z.string().optional(),
    status: z.number().int().optional(),
    errorCode: z.string().optional(),
    traceId: z.string().optional(),
    detail: z.string().optional(),
  })
  .passthrough();

export type ProblemDetails = z.infer<typeof problemDetailsSchema>;

export class ApiError extends Error {
  readonly status: number;
  readonly errorCode?: string;
  readonly traceId?: string;
  readonly details?: ProblemDetails;

  constructor(status: number, problem?: ProblemDetails) {
    super(problem?.title ?? `Yêu cầu thất bại (${status}).`);
    this.name = "ApiError";
    this.status = status;
    this.errorCode = problem?.errorCode;
    this.traceId = problem?.traceId;
    this.details = problem;
  }
}
