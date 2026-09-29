import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';
import { docsSchema } from '@astrojs/starlight/schema';

// The pages live directly in docs/ (not Starlight's default src/content/docs/), so the paths the
// repo already links to, such as docs/configuration.md, keep working.
export const collections = {
	docs: defineCollection({ loader: glob({ base: '.', pattern: '[^_]*.{md,mdx}' }), schema: docsSchema() }),
};
