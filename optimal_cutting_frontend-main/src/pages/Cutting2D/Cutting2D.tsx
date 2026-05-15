import { Alert, Button, Flex, Image, Table, Typography } from 'antd';
import { Cutting2DForm } from '../../components/forms/Cutting2DForm/Cutting2DForm';
import { useEffect, useState } from 'react';
import { useAppSelector } from '../../app/hooks';
import { selectCalculateData2D } from '../../features/cutting2DSlice';
import { Cutting2DNestingResult, NestingOutputPoint } from '../../types/Nesting2D';
import { getPNG2DCuttingPreview } from '../../functions/fetchFiles';
import { CuttingDownloadPanel } from '../../components/CuttingDownloadPanel/CuttingDownloadPanel';
import styles from './Cutting2D.module.css';

const DEFAULT_PREVIEW_SIZE = 1000;
const EMPTY_RESULT_MESSAGE = 'Детали не удалось разместить';

const downloadTextFile = (content: string, filename: string, type: string) => {
    const blob = new Blob([content], { type });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    link.click();
    URL.revokeObjectURL(url);
};

const getPreviewRequestBody = (cuttingData: Cutting2DNestingResult) =>
    JSON.stringify({
        workpieces: cuttingData.workpieces,
        totalPercentUsage: cuttingData.totalUtilization,
    });

const getPreviewSize = (cuttingData: Cutting2DNestingResult) => {
    const firstWorkpiece = cuttingData.workpieces[0];

    return {
        width: Math.max(firstWorkpiece?.width ?? DEFAULT_PREVIEW_SIZE, 1),
        height: Math.max(firstWorkpiece?.height ?? DEFAULT_PREVIEW_SIZE, 1),
    };
};

const getSvgMarkup = (svg: string) => {
    const trimmedSvg = svg.trim();
    const svgStart = trimmedSvg.indexOf('<svg');
    const svgEnd = trimmedSvg.lastIndexOf('</svg>');

    if (svgStart === -1) return '';
    if (svgEnd === -1) return trimmedSvg.slice(svgStart);

    return trimmedSvg.slice(svgStart, svgEnd + '</svg>'.length);
};

const isSafeSvg = (svg: string) => getSvgMarkup(svg).startsWith('<svg');

const withSvgViewBox = (svg: string, cuttingData: Cutting2DNestingResult) => {
    const normalizedSvg = getSvgMarkup(svg);

    if (!isSafeSvg(normalizedSvg) || normalizedSvg.includes('viewBox=')) {
        return normalizedSvg;
    }

    const { width, height } = getPreviewSize(cuttingData);
    return normalizedSvg.replace(
        '<svg ',
        `<svg width="${width}" height="${height}" viewBox="0 -${height} ${width} ${height}" `
    );
};

const getFallbackContours = (cuttingData: Cutting2DNestingResult) =>
    cuttingData.placedParts.flatMap((part) =>
        part.contours.map((contour) => ({
            id: part.partId,
            sheetId: part.sheetId,
            points: contour,
        }))
    );

const getContourBounds = (contours: { points: NestingOutputPoint[] }[]) => {
    const points = contours.flatMap((contour) => contour.points);

    if (points.length === 0) {
        return { minX: 0, minY: 0, width: DEFAULT_PREVIEW_SIZE, height: DEFAULT_PREVIEW_SIZE };
    }

    const xs = points.map((point) => point.x);
    const ys = points.map((point) => point.y);
    const minX = Math.min(...xs);
    const maxX = Math.max(...xs);
    const minY = Math.min(...ys);
    const maxY = Math.max(...ys);

    return {
        minX,
        minY,
        width: Math.max(maxX - minX, 1),
        height: Math.max(maxY - minY, 1),
    };
};

const FallbackPlacementPreview = ({ cuttingData }: { cuttingData: Cutting2DNestingResult }) => {
    const contours = getFallbackContours(cuttingData);

    if (contours.length === 0) return null;

    const bounds = getContourBounds(contours);
    const padding = Math.max(Math.max(bounds.width, bounds.height) * 0.03, 10);

    return (
        <svg
            className={styles.cutting2D__fallbackSvg}
            viewBox={`${bounds.minX - padding} ${bounds.minY - padding} ${
                bounds.width + padding * 2
            } ${bounds.height + padding * 2}`}
            role='img'
            aria-label='Схема размещения деталей'
        >
            {contours.map((contour, index) => (
                <polygon
                    key={`${contour.sheetId}-${contour.id}-${index}`}
                    points={contour.points
                        .map((point) => `${point.x},${point.y}`)
                        .join(' ')}
                    className={styles.cutting2D__fallbackPart}
                />
            ))}
        </svg>
    );
};

const Cutting2DDownload = ({
    hasValidSvg,
    previewSvg,
    dxf,
}: {
    hasValidSvg: boolean;
    previewSvg: string;
    dxf: string;
}) => (
    <CuttingDownloadPanel>
        <Button
            type='primary'
            danger
            disabled={!hasValidSvg}
            onClick={() => downloadTextFile(previewSvg, 'cutting-2d.svg', 'image/svg+xml')}
        >
            Скачать SVG
        </Button>
        <Button
            danger
            disabled={!dxf}
            onClick={() => downloadTextFile(dxf, 'cutting-2d.dxf', 'application/dxf')}
        >
            Скачать DXF
        </Button>
    </CuttingDownloadPanel>
);

const SchemePreview = ({
    cuttingData,
    hasValidSvg,
    previewImage,
    previewSvg,
}: {
    cuttingData: Cutting2DNestingResult;
    hasValidSvg: boolean;
    previewImage: string;
    previewSvg: string;
}) => (
    <Flex className={styles.cutting2D__scheme} align='center' justify='center'>
        {hasValidSvg ? (
            <div
                className={styles.cutting2D__svg}
                dangerouslySetInnerHTML={{ __html: previewSvg }}
            />
        ) : previewImage ? (
            <Image className={styles.cutting2D__image} src={previewImage} preview={false} />
        ) : (
            <Flex vertical className={styles.cutting2D__fallback}>
                <Typography.Text type='secondary'>
                    SVG в ответе отсутствует или имеет неожиданный формат. Показываем данные
                    размещения по placedParts.
                </Typography.Text>
                <FallbackPlacementPreview cuttingData={cuttingData} />
            </Flex>
        )}
    </Flex>
);

const ResultSummary = ({ cuttingData }: { cuttingData: Cutting2DNestingResult }) => {
    const sheetRows = Object.entries(cuttingData.utilizationBySheet).map(
        ([sheetId, usage], index) => ({
            key: sheetId,
            number: index + 1,
            sheetId,
            usage: `${usage.toFixed(2)}%`,
        })
    );

    const placementRows = cuttingData.placedParts.map((part, index) => ({
        key: `${part.sheetId}-${part.partId}-${index}`,
        partId: part.partId,
        sheetId: part.sheetId,
        x: part.x.toFixed(2),
        y: part.y.toFixed(2),
        rotation: `${part.rotation}°`,
    }));

    const summaryRows = [
        { label: 'К-во листов', value: cuttingData.sheets.length || sheetRows.length },
        { label: 'Использование', value: `${cuttingData.totalUtilization.toFixed(2)}%` },
        { label: 'Размещено деталей', value: cuttingData.placedParts.length },
        { label: 'Не размещено деталей', value: cuttingData.unplacedParts.length },
    ];

    return (
        <Flex vertical className={styles['table-result']}>
            <Flex className={styles['table-result__title']}>Результат</Flex>
            <Flex vertical className={styles.cutting2D__summaryList}>
                {summaryRows.map((row) => (
                    <Flex key={row.label} className={styles['table-result__count-workpiece']}>
                        {`${row.label} = ${row.value}`}
                    </Flex>
                ))}
            </Flex>

            {sheetRows.length > 0 && (
                <Table
                    columns={[
                        { title: 'Лист', dataIndex: 'number', key: 'number' },
                        { title: 'ID листа', dataIndex: 'sheetId', key: 'sheetId' },
                        { title: 'Использование', dataIndex: 'usage', key: 'usage' },
                    ]}
                    dataSource={sheetRows}
                    pagination={false}
                    className={styles.cutting2D__table}
                />
            )}

            <Table
                columns={[
                    { title: 'Деталь', dataIndex: 'partId', key: 'partId' },
                    { title: 'Лист', dataIndex: 'sheetId', key: 'sheetId' },
                    { title: 'X', dataIndex: 'x', key: 'x' },
                    { title: 'Y', dataIndex: 'y', key: 'y' },
                    { title: 'Поворот', dataIndex: 'rotation', key: 'rotation' },
                ]}
                dataSource={placementRows}
                pagination={false}
                className={styles.cutting2D__table}
            />

            {cuttingData.unplacedParts.length > 0 && (
                <Flex vertical className={styles.cutting2D__unplaced}>
                    <Typography.Text className={styles.cutting2D__unplacedTitle}>
                        Не удалось разместить детали:
                    </Typography.Text>
                    <Typography.Text type='warning'>
                        {cuttingData.unplacedParts.join(', ')}
                    </Typography.Text>
                </Flex>
            )}
        </Flex>
    );
};

const DebugResponseMessage = ({ cuttingData }: { cuttingData: Cutting2DNestingResult }) => {
    const keys = cuttingData.responseKeys ?? [];
    const diagnostics = cuttingData.diagnostics;

    return (
        <Alert
            type='warning'
            showIcon
            message='Расчет завершился успешно, но данные для отображения не найдены.'
            description={
                <Flex vertical gap={8}>
                    <span>{EMPTY_RESULT_MESSAGE}</span>
                    {cuttingData.unplacedParts.length > 0 && (
                        <span>Не размещены: {cuttingData.unplacedParts.join(', ')}</span>
                    )}
                    {diagnostics !== undefined && (
                        <pre className={styles.cutting2D__debug}>
                            {JSON.stringify(diagnostics, null, 2)}
                        </pre>
                    )}
                    {keys.length > 0 && <span>Ключи ответа: {keys.join(', ')}</span>}
                </Flex>
            }
        />
    );
};

export const Cutting2D = () => {
    const cuttingData = useAppSelector(selectCalculateData2D);
    const [previewImage, setPreviewImage] = useState('');
    const [previewImageError, setPreviewImageError] = useState('');
    const previewSvg = withSvgViewBox(cuttingData.svg, cuttingData);
    const hasValidSvg = isSafeSvg(previewSvg);
    const hasPlacements = cuttingData.placedParts.length > 0;
    const hasWorkpieces = cuttingData.workpieces.some(
        (workpiece) => workpiece.details.length > 0
    );
    const hasResponse = (cuttingData.responseKeys?.length ?? 0) > 0;
    const hasResult = previewImage.length > 0 || hasValidSvg || hasPlacements;

    useEffect(() => {
        let objectUrl = '';
        let isMounted = true;

        setPreviewImage('');
        setPreviewImageError('');

        if (!hasWorkpieces) return undefined;

        getPNG2DCuttingPreview(getPreviewRequestBody(cuttingData))
            .then((url) => {
                objectUrl = url;
                if (isMounted) setPreviewImage(url);
            })
            .catch(() => {
                if (isMounted) {
                    setPreviewImageError('Не удалось загрузить PNG-превью раскроя.');
                }
            });

        return () => {
            isMounted = false;
            if (objectUrl) URL.revokeObjectURL(objectUrl);
        };
    }, [cuttingData, hasWorkpieces]);

    return (
        <Flex className={styles.cutting2D}>
            <Cutting2DForm />
            <Flex vertical className={styles.cutting2D__result}>
                {hasResult ? (
                    <>
                        <SchemePreview
                            cuttingData={cuttingData}
                            hasValidSvg={hasValidSvg}
                            previewImage={previewImage}
                            previewSvg={previewSvg}
                        />
                        {previewImageError && (
                            <Alert type='warning' showIcon message={previewImageError} />
                        )}
                        <ResultSummary cuttingData={cuttingData} />
                    </>
                ) : hasResponse ? (
                    <DebugResponseMessage cuttingData={cuttingData} />
                ) : (
                    <Flex className={styles.cutting2D__empty} align='center' justify='center'>
                        Выберите детали и заготовку, чтобы построить схему 2D-раскроя.
                    </Flex>
                )}
            </Flex>
            {hasResult && (
                <Cutting2DDownload
                    hasValidSvg={hasValidSvg}
                    previewSvg={previewSvg}
                    dxf={cuttingData.dxf}
                />
            )}
        </Flex>
    );
};
