"use client";

import { useParams } from "next/navigation";
import { JobDetails } from "@/components/job-details";

/**
 * Reads the public UUID from /jobs/[jobId] and mounts JobDetails. The key resets local mutation
 * state when navigation switches to another job.
 */
export default function JobPage() {
  const { jobId } = useParams<{ jobId: string }>();
  return <JobDetails key={jobId} jobId={jobId} />;
}
