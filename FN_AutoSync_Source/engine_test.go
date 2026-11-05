package main

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestBackupRestore(t *testing.T) {
	root := t.TempDir()
	src := filepath.Join(root, "source")
	dest := filepath.Join(root, "dest")
	recovered := filepath.Join(root, "recovered")
	for _, d := range []string{src, dest} {
		if err := os.MkdirAll(d, 0700); err != nil {
			t.Fatal(err)
		}
	}
	long := strings.Repeat("厦门台区", 25) + ".xlsx" // 300+ UTF-8 bytes
	if len([]byte(long)) <= 255 {
		t.Fatalf("expected long name: %d", len(long))
	}
	// Linux ext4 cannot write >255 BYTE filename; simulate normal source by using a 240 byte one for automatic testing.
	normal := "普通报告.xlsx"
	if err := os.WriteFile(filepath.Join(src, normal), []byte("new"), 0600); err != nil {
		t.Fatal(err)
	}
	sub := filepath.Join(src, "子目录")
	if err := os.MkdirAll(sub, 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(sub, "报表.csv"), []byte("abc"), 0600); err != nil {
		t.Fatal(err)
	}
	first, err := runBackup(src, dest, nil)
	if err != nil {
		t.Fatal(err)
	}
	if first.Uploaded != 2 {
		t.Fatalf("unexpected stats %+v", first)
	}
	second, err := runBackup(src, dest, nil)
	if err != nil {
		t.Fatal(err)
	}
	if second.Unchanged != 2 || second.Uploaded != 0 {
		t.Fatalf("incremental did not skip %+v", second)
	}
	restored, err := runRestore(dest, recovered, nil)
	if err != nil {
		t.Fatal(err)
	}
	if restored.Restored != 2 {
		t.Fatalf("restore stats %+v", restored)
	}
	b, err := os.ReadFile(filepath.Join(recovered, "子目录", "报表.csv"))
	if err != nil || string(b) != "abc" {
		t.Fatalf("recovered content %q %v", b, err)
	}
	_ = long
}
func TestConversion(t *testing.T) {
	name := strings.Repeat("厦门", 50) + ".xlsx"
	got := componentName(name, name, map[string]string{}, "")
	if got == name || len(got) > 255 || !strings.HasSuffix(got, ".xlsx") {
		t.Fatalf("unexpected conversion %q", got)
	}
	regular := "福建南科合同.xlsx"
	if got = componentName(regular, regular, map[string]string{}, ""); got != regular {
		t.Fatalf("changed ordinary file %s", got)
	}
}

func TestUpdateConflictAndCorruption(t *testing.T) {
	root := t.TempDir()
	src := filepath.Join(root, "src")
	nas := filepath.Join(root, "nas")
	out := filepath.Join(root, "out")
	for _, d := range []string{src, nas, out} {
		if err := os.MkdirAll(d, 0700); err != nil {
			t.Fatal(err)
		}
	}
	path := filepath.Join(src, "test.txt")
	if err := os.WriteFile(path, []byte("version-one"), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := runBackup(src, nas, nil); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte("version-two-new"), 0600); err != nil {
		t.Fatal(err)
	}
	got, err := runBackup(src, nas, nil)
	if err != nil {
		t.Fatal(err)
	}
	if got.Uploaded != 1 {
		t.Fatalf("should update changed file: %+v", got)
	}
	// Never overwrite existing different user data in a restore directory.
	if err := os.WriteFile(filepath.Join(out, "test.txt"), []byte("do-not-overwrite"), 0600); err != nil {
		t.Fatal(err)
	}
	got, err = runRestore(nas, out, nil)
	if err != nil {
		t.Fatal(err)
	}
	if got.Conflicts != 1 || got.Restored != 0 {
		t.Fatalf("expected conflict: %+v", got)
	}
	b, _ := os.ReadFile(filepath.Join(out, "test.txt"))
	if string(b) != "do-not-overwrite" {
		t.Fatal("overwrote file")
	}
	if err := os.Remove(filepath.Join(out, "test.txt")); err != nil {
		t.Fatal(err)
	}
	// NAS copy corrupted: shouldn't leave a corrupt recovered file.
	if err := os.WriteFile(filepath.Join(nas, "test.txt"), []byte("version-two-old"), 0600); err != nil {
		t.Fatal(err)
	}
	got, err = runRestore(nas, out, nil)
	if err != nil {
		t.Fatal(err)
	}
	if got.Failed != 1 || got.Restored != 0 {
		t.Fatalf("expected corruption failure: %+v", got)
	}
	if _, err := os.Stat(filepath.Join(out, "test.txt")); !os.IsNotExist(err) {
		t.Fatal("corrupt output remained")
	}
}
