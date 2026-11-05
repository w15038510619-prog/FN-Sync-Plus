package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
	"unicode/utf8"
)

const manifestName = "__FN_ORIGINAL_NAMES__.json"
const manifestVersion = 1

type Entry struct {
	Original string `json:"original"`
	Stored   string `json:"stored"`
	Size     int64  `json:"size"`
	ModTime  int64  `json:"mtime_ns"`
	SHA256   string `json:"sha256"`
}
type DirEntry struct {
	Original string `json:"original"`
	Stored   string `json:"stored"`
}
type Manifest struct {
	Version     int        `json:"version"`
	UpdatedAt   string     `json:"updated_at"`
	Note        string     `json:"note"`
	Files       []Entry    `json:"files"`
	Directories []DirEntry `json:"directories"`
}
type RunSummary struct{ Total, Uploaded, Unchanged, Failed, Restored, Conflicts int }
type Logger func(string)

func note(l Logger, format string, args ...interface{}) {
	if l != nil {
		l(fmt.Sprintf(format, args...))
	}
}
func normalizeRel(s string) string { return filepath.ToSlash(filepath.Clean(s)) }
func safeRel(s string) bool {
	if s == "" || s == "." || filepath.IsAbs(s) {
		return false
	}
	clean := filepath.Clean(filepath.FromSlash(s))
	return clean != ".." && !strings.HasPrefix(clean, ".."+string(os.PathSeparator))
}
func hashedName(s, ext string, n int) string {
	b := sha256.Sum256([]byte(s))
	hx := hex.EncodeToString(b[:])
	if n > len(hx) {
		n = len(hx)
	}
	if utf8.RuneCountInString(ext) > 30 || len(ext) > 50 {
		ext = ""
	}
	return "_FN_LONG_" + hx[:n] + ext
}
func componentName(name, originalRel string, occupied map[string]string, was string) string {
	// Case-insensitive collisions on Windows/SMB; leave every ordinary valid filename unchanged.
	if was != "" && len([]byte(was)) <= 255 {
		if o, ok := occupied[strings.ToLower(was)]; !ok || o == originalRel {
			occupied[strings.ToLower(was)] = originalRel
			return was
		}
	}
	if len([]byte(name)) <= 255 && name != manifestName {
		if o, ok := occupied[strings.ToLower(name)]; !ok || o == originalRel {
			occupied[strings.ToLower(name)] = originalRel
			return name
		}
	}
	ext := filepath.Ext(name)
	for n := 24; n <= 64; n += 8 {
		alias := hashedName(originalRel, ext, n)
		if o, ok := occupied[strings.ToLower(alias)]; !ok || o == originalRel {
			occupied[strings.ToLower(alias)] = originalRel
			return alias
		}
	}
	panic("SHA-256 hash collision")
}
func loadManifest(dest string) (Manifest, error) {
	path := filepath.Join(dest, manifestName)
	b, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return Manifest{Version: manifestVersion, Note: "文件名映射，请与备份一起保存，切勿删除。"}, nil
	}
	if err != nil {
		return Manifest{}, err
	}
	var m Manifest
	if err := json.Unmarshal(b, &m); err != nil {
		return m, fmt.Errorf("映射文件损坏，已停止操作以避免丢失原名: %w", err)
	}
	if m.Version != manifestVersion {
		return m, fmt.Errorf("暂不支持映射文件版本 %d", m.Version)
	}
	return m, nil
}
func writeManifest(dest string, m Manifest) error {
	m.Version = manifestVersion
	m.UpdatedAt = time.Now().Format(time.RFC3339)
	if m.Note == "" {
		m.Note = "文件名映射，请与备份一起保存，切勿删除。"
	}
	b, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return err
	}
	temp := filepath.Join(dest, manifestName+".new")
	if err = os.WriteFile(temp, b, 0600); err != nil {
		return err
	}
	// Keep previous file until new version is safely written.
	old := filepath.Join(dest, manifestName)
	prev := old + ".previous"
	_ = os.Remove(prev)
	if _, err := os.Stat(old); err == nil {
		if err = os.Rename(old, prev); err != nil {
			return fmt.Errorf("保存映射备份失败: %w", err)
		}
	}
	if err = os.Rename(temp, old); err != nil {
		_ = os.Rename(prev, old)
		return err
	}
	return nil
}
func isNestedPath(parent, child string) bool {
	p, err := filepath.Abs(parent)
	if err != nil {
		return false
	}
	c, err := filepath.Abs(child)
	if err != nil {
		return false
	}
	p = filepath.Clean(p)
	c = filepath.Clean(c)
	if strings.EqualFold(p, c) {
		return true
	}
	rel, err := filepath.Rel(p, c)
	return err == nil && rel != ".." && !strings.HasPrefix(rel, ".."+string(os.PathSeparator))
}
func validatePaths(src, dest string) error {
	if src == "" || dest == "" {
		return errors.New("请先选择电脑文件夹和 NAS 共享文件夹")
	}
	if isNestedPath(src, dest) || isNestedPath(dest, src) {
		return errors.New("电脑文件夹与 NAS 文件夹不能相同、也不能互相包含")
	}
	st, err := os.Stat(src)
	if err != nil {
		return fmt.Errorf("无法读取电脑文件夹: %w", err)
	}
	if !st.IsDir() {
		return errors.New("电脑源路径不是文件夹")
	}
	st, err = os.Stat(dest)
	if err != nil {
		return fmt.Errorf("无法访问 NAS 目标文件夹（请先开启 SMB 共享并保证可以访问）: %w", err)
	}
	if !st.IsDir() {
		return errors.New("NAS 目标路径不是文件夹")
	}
	return nil
}
func copyWithDigest(src, dest string, mtime time.Time) (string, error) {
	input, err := os.Open(src)
	if err != nil {
		return "", err
	}
	defer input.Close()
	tmp := dest + ".fn-partial"
	// Unique temp per target; only one job allowed.
	out, err := os.OpenFile(tmp, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if errors.Is(err, os.ErrExist) {
		_ = os.Remove(tmp)
		out, err = os.OpenFile(tmp, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	}
	if err != nil {
		return "", err
	}
	good := false
	defer func() {
		_ = out.Close()
		if !good {
			_ = os.Remove(tmp)
		}
	}()
	hash := sha256.New()
	if _, err = io.CopyBuffer(io.MultiWriter(out, hash), input, make([]byte, 256*1024)); err != nil {
		return "", err
	}
	if err = out.Sync(); err != nil {
		return "", err
	}
	if err = out.Close(); err != nil {
		return "", err
	}
	_ = os.Chtimes(tmp, mtime, mtime)
	// Make a rollback copy by renaming existing destination before committing the new file.
	old := dest + ".fn-prior"
	_ = os.Remove(old)
	prev := false
	if _, err = os.Stat(dest); err == nil {
		if err = os.Rename(dest, old); err != nil {
			return "", err
		}
		prev = true
	}
	if err = os.Rename(tmp, dest); err != nil {
		if prev {
			_ = os.Rename(old, dest)
		}
		return "", err
	}
	good = true
	if prev {
		_ = os.Remove(old)
	}
	return hex.EncodeToString(hash.Sum(nil)), nil
}
func sortedEntries(in []Entry) {
	sort.Slice(in, func(i, j int) bool { return in[i].Original < in[j].Original })
}
func sortedDirs(in []DirEntry) {
	sort.Slice(in, func(i, j int) bool { return in[i].Original < in[j].Original })
}
func runBackup(src, dest string, log Logger) (RunSummary, error) {
	var stat RunSummary
	if err := validatePaths(src, dest); err != nil {
		return stat, err
	}
	m, err := loadManifest(dest)
	if err != nil {
		return stat, err
	}
	prevFiles := make(map[string]Entry)
	prevDirs := make(map[string]DirEntry)
	for _, e := range m.Files {
		prevFiles[e.Original] = e
	}
	for _, d := range m.Directories {
		prevDirs[d.Original] = d
	}
	// Reserve existing source->stored identities, never reassign an old alias to a different source.
	used := make(map[string]map[string]string)
	reserve := func(parent, alias, original string) {
		if used[parent] == nil {
			used[parent] = make(map[string]string)
		}
		used[parent][strings.ToLower(alias)] = original
	}
	for _, e := range m.Files {
		reserve(normalizeRel(filepath.Dir(filepath.FromSlash(e.Stored))), filepath.Base(filepath.FromSlash(e.Stored)), e.Original)
	}
	for _, d := range m.Directories {
		reserve(normalizeRel(filepath.Dir(filepath.FromSlash(d.Stored))), filepath.Base(filepath.FromSlash(d.Stored)), d.Original)
	}
	dirMapping := map[string]string{".": "."}
	// Directory walk is lexical, so parents are visited first.
	err = filepath.WalkDir(src, func(full string, d fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			stat.Failed++
			note(log, "无法访问: %s (%v)", full, walkErr)
			return nil
		}
		rel, err := filepath.Rel(src, full)
		if err != nil {
			stat.Failed++
			note(log, "路径错误: %v", err)
			return nil
		}
		if rel == "." {
			return nil
		}
		rel = normalizeRel(rel)
		parentRel := normalizeRel(filepath.Dir(filepath.FromSlash(rel)))
		mappedParent, ok := dirMapping[parentRel]
		if !ok {
			stat.Failed++
			note(log, "跳过: 父文件夹无法访问 %s", rel)
			if d.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		if d.Type()&os.ModeSymlink != 0 {
			note(log, "跳过快捷链接/符号链接: %s", rel)
			if d.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		name := d.Name()
		oldAlias := ""
		if d.IsDir() {
			if old, ok := prevDirs[rel]; ok {
				oldAlias = filepath.Base(filepath.FromSlash(old.Stored))
			}
		} else if old, ok := prevFiles[rel]; ok {
			oldAlias = filepath.Base(filepath.FromSlash(old.Stored))
		}
		occupied := used[mappedParent]
		if occupied == nil {
			occupied = make(map[string]string)
			used[mappedParent] = occupied
		}
		alias := componentName(name, rel, occupied, oldAlias)
		mappedRel := normalizeRel(filepath.Join(filepath.FromSlash(mappedParent), alias))
		target := filepath.Join(dest, filepath.FromSlash(mappedRel))
		if d.IsDir() {
			if err = os.MkdirAll(target, 0700); err != nil {
				stat.Failed++
				note(log, "创建目录失败: %s (%v)", rel, err)
				return filepath.SkipDir
			}
			dirMapping[rel] = mappedRel
			prevDirs[rel] = DirEntry{Original: rel, Stored: mappedRel}
			return nil
		}
		if !d.Type().IsRegular() {
			note(log, "跳过特殊文件: %s", rel)
			return nil
		}
		stat.Total++
		info, err := d.Info()
		if err != nil {
			stat.Failed++
			note(log, "读取属性失败: %s", rel)
			return nil
		}
		old, ok := prevFiles[rel]
		if ok && old.Stored == mappedRel && old.Size == info.Size() && old.ModTime == info.ModTime().UnixNano() && old.SHA256 != "" {
			if st, er := os.Stat(target); er == nil && st.Size() == old.Size {
				stat.Unchanged++
				return nil
			}
		}
		if err = os.MkdirAll(filepath.Dir(target), 0700); err != nil {
			stat.Failed++
			note(log, "创建目录失败: %s (%v)", rel, err)
			return nil
		}
		digest, err := copyWithDigest(full, target, info.ModTime())
		if err != nil {
			stat.Failed++
			note(log, "上传失败: %s (%v)", rel, err)
			return nil
		}
		prevFiles[rel] = Entry{Original: rel, Stored: mappedRel, Size: info.Size(), ModTime: info.ModTime().UnixNano(), SHA256: digest}
		stat.Uploaded++
		if name != alias {
			note(log, "转换超长名称: %s → %s", name, alias)
		} else {
			note(log, "已备份: %s", rel)
		}
		return nil
	})
	if err != nil {
		return stat, err
	}
	m.Files = m.Files[:0]
	for _, e := range prevFiles {
		m.Files = append(m.Files, e)
	}
	m.Directories = m.Directories[:0]
	for _, d := range prevDirs {
		m.Directories = append(m.Directories, d)
	}
	sortedEntries(m.Files)
	sortedDirs(m.Directories)
	if err = writeManifest(dest, m); err != nil {
		return stat, fmt.Errorf("文件已上传，但无法保存原名映射，须修复后才能安全恢复: %w", err)
	}
	note(log, "同步结束：扫描 %d，新增/更新 %d，跳过未变化 %d，失败 %d", stat.Total, stat.Uploaded, stat.Unchanged, stat.Failed)
	return stat, nil
}
func runRestore(nas, target string, log Logger) (RunSummary, error) {
	var stat RunSummary
	m, err := loadManifest(nas)
	if err != nil {
		return stat, err
	}
	if len(m.Files) == 0 && len(m.Directories) == 0 {
		return stat, errors.New("此 NAS 文件夹没有可恢复的原名映射，或备份为空")
	}
	if target == "" {
		return stat, errors.New("请选择要恢复到的电脑文件夹")
	}
	if isNestedPath(nas, target) || isNestedPath(target, nas) {
		return stat, errors.New("恢复目录不能位于 NAS 备份目录内，也不能包含 NAS 备份目录")
	}
	if err = os.MkdirAll(target, 0700); err != nil {
		return stat, err
	}
	sort.Slice(m.Directories, func(i, j int) bool { return len(m.Directories[i].Original) < len(m.Directories[j].Original) })
	for _, d := range m.Directories {
		if !safeRel(d.Original) || !safeRel(d.Stored) {
			stat.Failed++
			note(log, "安全检查失败，跳过目录: %s", d.Original)
			continue
		}
		if err = os.MkdirAll(filepath.Join(target, filepath.FromSlash(d.Original)), 0700); err != nil {
			stat.Failed++
			note(log, "恢复目录失败: %s (%v)", d.Original, err)
		}
	}
	for _, e := range m.Files {
		stat.Total++
		if !safeRel(e.Original) || !safeRel(e.Stored) {
			stat.Failed++
			note(log, "安全检查失败，跳过文件: %s", e.Original)
			continue
		}
		from := filepath.Join(nas, filepath.FromSlash(e.Stored))
		to := filepath.Join(target, filepath.FromSlash(e.Original))
		if err = os.MkdirAll(filepath.Dir(to), 0700); err != nil {
			stat.Failed++
			note(log, "创建父目录失败: %s (%v)", e.Original, err)
			continue
		}
		if st, er := os.Stat(to); er == nil {
			if st.Size() == e.Size {
				if digest, err := shaFile(to); err == nil && digest == e.SHA256 {
					stat.Unchanged++
					continue
				}
			}
			stat.Conflicts++
			note(log, "发现同名不同内容，未覆盖: %s", e.Original)
			continue
		}
		st, err := os.Stat(from)
		if err != nil {
			stat.Failed++
			note(log, "NAS 缺少文件: %s (%v)", e.Stored, err)
			continue
		}
		if st.Size() != e.Size {
			stat.Failed++
			note(log, "大小校验失败: %s", e.Stored)
			continue
		}
		digest, err := copyWithDigest(from, to, time.Unix(0, e.ModTime))
		if err != nil {
			stat.Failed++
			note(log, "恢复失败: %s (%v)", e.Original, err)
			continue
		}
		if digest != e.SHA256 {
			_ = os.Remove(to)
			stat.Failed++
			note(log, "SHA-256 校验失败，已删除损坏的恢复副本: %s", e.Original)
			continue
		}
		stat.Restored++
		note(log, "已恢复: %s", e.Original)
	}
	note(log, "恢复结束：已恢复 %d，已存在同内容 %d，冲突未覆盖 %d，失败 %d", stat.Restored, stat.Unchanged, stat.Conflicts, stat.Failed)
	return stat, nil
}
func shaFile(path string) (string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer f.Close()
	h := sha256.New()
	_, err = io.Copy(h, f)
	if err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}
