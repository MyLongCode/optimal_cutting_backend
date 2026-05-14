import { Alert, Button, Card, Flex, Image, Typography } from 'antd';
import { Cutting2DForm } from '../../components/forms/Cutting2DForm/Cutting2DForm';
import { useEffect, useState } from 'react';
import { useAppSelector } from '../../app/hooks';
import { selectCalculateData2D } from '../../features/cutting2DSlice';
import { Cutting2DNestingResult, NestingOutputPoint } from '../../types/Nesting2D';
import { getPNG2DCuttingPreview } from '../../functions/fetchFiles';
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

const isSafeSvg = (svg: string) => svg.trimStart().startsWith('<svg');

const withSvgViewBox = (svg: string, cuttingData: Cutting2DNestingResult) => {
    const normalizedSvg = svg.trim();

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

const PlacementTable = ({ cuttingData }: { cuttingData: Cutting2DNestingResult }) => (
    <div className={styles.cutting2D__placements}>
        <Typography.Title level={5}>Данные размещения</Typography.Title>
        <table>
            <thead>
                <tr>
                    <th>Деталь</th>
                    <th>Лист</th>
                    <th>X</th>
                    <th>Y</th>
                    <th>Поворот</th>
                </tr>
            </thead>
            <tbody>
                {cuttingData.placedParts.map((part, index) => (
                    <tr key={`${part.sheetId}-${part.partId}-${index}`}>
                        <td>{part.partId}</td>
                        <td>{part.sheetId}</td>
                        <td>{part.x.toFixed(2)}</td>
                        <td>{part.y.toFixed(2)}</td>
                        <td>{part.rotation}°</td>
                    </tr>
                ))}
            </tbody>
        </table>
    </div>
);

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
        <Flex className={styles.cutting2D} gap={24}>
            <Cutting2DForm />
            <Flex vertical className={styles.cutting2D__result} gap={16}>
                {hasResult ? (
                    <>
                        <Card className={styles.cutting2D__summary}>
                            <Flex gap={24} wrap='wrap'>
                                <Typography.Text>
                                    Размещено: {cuttingData.placedParts.length}
                                </Typography.Text>
                                <Typography.Text>
                                    Не размещено: {cuttingData.unplacedParts.length}
                                </Typography.Text>
                                <Typography.Text>
                                    Использование: {cuttingData.totalUtilization.toFixed(2)}%
                                </Typography.Text>
                            </Flex>
                            {cuttingData.unplacedParts.length > 0 && (
                                <Typography.Text type='warning'>
                                    Не удалось разместить детали:{' '}
                                    {cuttingData.unplacedParts.join(', ')}
                                </Typography.Text>
                            )}
                        </Card>
                        <Flex gap={12}>
                            <Button
                                disabled={!hasValidSvg}
                                onClick={() =>
                                    downloadTextFile(
                                        previewSvg,
                                        'cutting-2d.svg',
                                        'image/svg+xml'
                                    )
                                }
                            >
                                Скачать SVG
                            </Button>
                            <Button
                                disabled={!cuttingData.dxf}
                                onClick={() =>
                                    downloadTextFile(
                                        cuttingData.dxf,
                                        'cutting-2d.dxf',
                                        'application/dxf'
                                    )
                                }
                            >
                                Скачать DXF
                            </Button>
                        </Flex>
                        {previewImageError && (
                            <Alert type='warning' showIcon message={previewImageError} />
                        )}
                        {previewImage ? (
                            <Image
                                className={styles.cutting2D__image}
                                src={previewImage}
                                preview={false}
                            />
                        ) : hasValidSvg ? (
                            <div
                                className={styles.cutting2D__svg}
                                dangerouslySetInnerHTML={{ __html: previewSvg }}
                            />
                        ) : (
                            <Card className={styles.cutting2D__fallback}>
                                <Typography.Text type='secondary'>
                                    SVG в ответе отсутствует или имеет неожиданный формат.
                                    Показываем данные размещения по placedParts.
                                </Typography.Text>
                                <FallbackPlacementPreview cuttingData={cuttingData} />
                                <PlacementTable cuttingData={cuttingData} />
                            </Card>
                        )}
                    </>
                ) : hasResponse ? (
                    <DebugResponseMessage cuttingData={cuttingData} />
                ) : (
                    <Flex className={styles.cutting2D__empty} align='center' justify='center'>
                        Выберите детали и заготовку, чтобы построить схему 2D-раскроя.
                    </Flex>
                )}
            </Flex>
        </Flex>
    );
};
