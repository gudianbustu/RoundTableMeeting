import { GithubOutlined } from '@ant-design/icons'

export function SourceRepositoryLink() {
  return <a className="source-repository-link" href="https://github.com/gudianbustu/RoundTableMeeting"
    target="_blank" rel="noopener noreferrer" title="GitHub 源码" aria-label="GitHub 源码（在新标签页打开）">
    <GithubOutlined aria-hidden="true" />
  </a>
}
