import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { StartCrawlForm } from "@/components/start-crawl-form";

/**
 * Renders the landing-page shell. StartCrawlForm owns the browser state, validation, and
 * create-job request.
 */
export default function HomePage() {
  return (
    <div className="max-w-2xl py-5 sm:py-12">
      <p className="text-sm font-medium text-muted-foreground">
        Website explorer
      </p>
      <h1 className="my-4 text-4xl leading-tight font-semibold tracking-tight sm:text-6xl">
        See how a website connects.
      </h1>
      <p className="text-lg leading-relaxed text-muted-foreground">
        Explore a website’s pages, follow their links, and see which links stay
        within the starting domain.
      </p>
      <Card className="mt-8 max-w-xl">
        <CardHeader>
          <CardTitle>
            <h2>Start a crawl</h2>
          </CardTitle>
          <CardDescription>
            Enter a public website URL. We’ll follow HTML links on its starting
            host.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <StartCrawlForm />
        </CardContent>
      </Card>
    </div>
  );
}
