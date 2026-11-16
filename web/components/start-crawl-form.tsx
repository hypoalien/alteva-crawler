"use client";

import { useMutation } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import type { FormEvent } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ErrorNotice } from "@/components/error-notice";
import { createJob } from "@/lib/api";

/**
 * Owns URL/depth input and the create-job mutation. Disables repeat submission while pending,
 * displays errors, and navigates to the returned public job UUID.
 */
export function StartCrawlForm() {
  const router = useRouter();
  // A mutation represents a server-side change. Navigation happens only after the API confirms creation.
  const mutation = useMutation({
    mutationFn: createJob,
    onSuccess: ({ jobId }) => router.push(`/jobs/${encodeURIComponent(jobId)}`),
  });

  /**
   * Converts form fields into the API payload; an empty depth stays omitted so the server
   * applies its default. Prevents the browser from doing a normal full-page form submission.
   */
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const depth = String(form.get("maxDepth") ?? "");
    mutation.mutate({
      url: String(form.get("url") ?? "").trim(),
      maxDepth: depth === "" ? undefined : Number(depth),
    });
  }

  return (
    <form onSubmit={submit} className="space-y-5">
      <fieldset disabled={mutation.isPending} className="space-y-5">
        <div className="space-y-2">
          <Label htmlFor="url">Website URL</Label>
          <Input
            id="url"
            name="url"
            type="url"
            placeholder="https://example.com"
            required
            maxLength={2000}
            autoComplete="url"
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="maxDepth">Maximum depth</Label>
          <Input
            id="maxDepth"
            name="maxDepth"
            type="number"
            min={0}
            max={5}
            step={1}
            defaultValue={2}
            aria-describedby="depth-help"
            className="max-w-24"
          />
          <p id="depth-help" className="text-sm text-muted-foreground">
            The starting page is depth 0. Choose up to 5.
          </p>
        </div>
        <Button type="submit">
          {mutation.isPending ? "Starting…" : "Start crawl"}
        </Button>
      </fieldset>
      {mutation.error ? <ErrorNotice error={mutation.error} /> : null}
    </form>
  );
}
