// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import { satteri } from '@astrojs/markdown-satteri';

// Pages link to each other as `page.md#section`, which also works when browsing docs/ on GitHub.
// On the site, those become `/page/#section`.
const pageLinks = {
	name: 'page-links',
	link(node, ctx) {
		const m = /^([a-z0-9-]+)\.mdx?(#.*)?$/.exec(node.url);
		if (m) ctx.setProperty(node, 'url', m[1] === 'index' ? `/${m[2] ?? ''}` : `/${m[1]}/${m[2] ?? ''}`);
	},
};

export default defineConfig({
	site: 'https://fuseraft.ai',
	markdown: { processor: satteri({ mdastPlugins: [pageLinks] }) },
	integrations: [
		starlight({
			title: 'fuseraft',
			description: 'A terminal AI assistant that scales into multi-agent pipelines, built on Microsoft Agent Framework.',
			logo: { src: './src/assets/logo.svg' },
			favicon: '/favicon.svg',
			social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/fuseraft/fuseraft-cli' }],
			editLink: { baseUrl: 'https://github.com/fuseraft/fuseraft-cli/edit/main/docs/' },
			lastUpdated: true,
			// The pages live in docs/ itself rather than src/content/docs/ (see src/content.config.ts).
			markdown: { processedDirs: ['./'] },
			customCss: ['./src/styles/custom.css'],
			sidebar: [
				{
					label: 'Start here',
					items: [
						{ label: 'Getting Started', link: '/getting-started/' },
						{ label: 'REPL', link: '/repl/' },
						{ label: 'Serve (Daemon Mode)', link: '/serve/' },
						{ label: 'Writing Tasks', link: '/writing-tasks/' },
						{ label: 'Examples', link: '/examples/' },
					],
				},
				{
					label: 'Agent teams',
					items: [
						{ label: 'Configuration', link: '/configuration/' },
						{ label: 'Models & Providers', link: '/models/' },
						{ label: 'Strategies', link: '/strategies/' },
						{ label: 'Routing Validators', link: '/validators/' },
						{ label: 'Harness Engineering', link: '/harness-engineering/' },
						{ label: 'Spec-Driven Development', link: '/spec-driven/' },
						{ label: 'Subagents', link: '/subagents/' },
					],
				},
				{
					label: 'Tools & extensions',
					items: [
						{ label: 'Plugins', link: '/plugins/' },
						{ label: 'Skills', link: '/skills/' },
						{ label: 'MCP', link: '/mcp/' },
					],
				},
				{
					label: 'Sessions & context',
					items: [
						{ label: 'Sessions', link: '/sessions/' },
						{ label: 'Context Management', link: '/context-management/' },
						{ label: 'Context Store', link: '/context-store/' },
						{ label: 'Knowledge Layer', link: '/knowledge/' },
					],
				},
				{
					label: 'Safety & quality',
					items: [
						{ label: 'Security & Sandbox', link: '/security/' },
						{ label: 'Governance', link: '/governance/' },
						{ label: 'Evals', link: '/evals/' },
					],
				},
				{
					label: 'Reference',
					items: [
						{ label: 'CLI Reference', link: '/cli-reference/' },
						{ label: 'Scripting & Automation', link: '/scripting/' },
						{ label: 'Design', link: '/design/' },
					],
				},
			],
		}),
	],
});
